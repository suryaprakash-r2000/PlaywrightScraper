using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;

string environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production;
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

string baseApiUrl = configuration["ScraperSettings:BaseApiUrl"]!;
string stateToProcess = configuration["ScraperSettings:StateToProcess"]!;
string targetRateSchedule = configuration["ScraperSettings:TargetRateSchedule"]!;
string homeUrl = configuration["ScraperSettings:HomeUrl"]!;
int maxConcurrency = configuration.GetValue<int>("ScraperSettings:MaxConcurrency", 5);

// ---- Logging setup ----
string logFolder = configuration["ScraperSettings:LogFolder"] ?? Path.Combine(AppContext.BaseDirectory, "Logs");
Logger.Init(logFolder);
Logger.Log($"=== Scraper run started (state={stateToProcess}, rateSchedule={targetRateSchedule}, concurrency={maxConcurrency}) ===");

var csvRows = new ConcurrentBag<string>();
using var httpClient = new HttpClient { BaseAddress = new Uri(baseApiUrl), Timeout = TimeSpan.FromSeconds(30) };

Logger.Log($"Fetching active customer leads for state: {stateToProcess}...");
List<CustomerLeadDto> customerLeads;
try
{
    var response = await httpClient.GetFromJsonAsync<List<CustomerLeadDto>>($"api/PTCPricingProcess/state/{stateToProcess}");
    customerLeads = response ?? new List<CustomerLeadDto>();
    if (!customerLeads.Any())
    {
        Logger.Log("No customer leads found for this state. Exiting process.");
        return;
    }
    Logger.Log($"Found {customerLeads.Count} unique Zip/Utility combinations to process.");
}
catch (Exception ex)
{
    Logger.Log($"Failed to retrieve zip codes from API: {ex.Message}");
    return;
}

try
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true
    });

    using var throttle = new SemaphoreSlim(maxConcurrency);

    // CHANGE: Parallelize across every lead combination, not just unique Zips
    var tasks = customerLeads.Select(async lead =>
    {
        await throttle.WaitAsync();
        try
        {
            await ProcessLeadAsync(browser, lead, csvRows, targetRateSchedule, homeUrl, httpClient);
        }
        catch (Exception leadEx)
        {
            Logger.Log($"Unhandled error while processing zip {lead.ZipCode} / {lead.UtilityProvider}: {leadEx.Message}");
        }
        finally
        {
            throttle.Release();
        }
    });

    await Task.WhenAll(tasks);
    await browser.CloseAsync();
}
catch (Exception ex)
{
    Logger.Log($"Fatal error during scraping run: {ex.Message}");
}
finally
{
    try
    {
        var csv = new StringBuilder();
        csv.AppendLine("Utility,ZipCode,ServiceType,PricePerKwh,EstimatedMonthlyBill,LastUpdated");
        foreach (var row in csvRows)
        {
            csv.AppendLine(row);
        }
        await File.WriteAllTextAsync("PowerRates.csv", csv.ToString());
        Logger.Log("Completed.");
        Logger.Log("CSV saved as PowerRates.csv");
    }
    catch (Exception ex)
    {
        Logger.Log($"Failed to write CSV file: {ex.Message}");
    }
    Logger.Log("=== Scraper run finished ===");
}

// CHANGE: Rewritten to process a single explicit Zip + Utility target
async Task ProcessLeadAsync(IBrowser browser, CustomerLeadDto lead, ConcurrentBag<string> csvRows, string targetRate, string targetUrl, HttpClient apiHttpClient)
{
    await using var context = await browser.NewContextAsync();
    var page = await context.NewPageAsync();
    var success = false;

    for (int attempt = 1; attempt <= 2 && !success; attempt++)
    {
        try
        {
            Logger.Log($"Processing Zip: {lead.ZipCode}, Utility: {lead.UtilityProvider} (attempt {attempt})");
            await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 45000
            });

            var zipInput = page.Locator("#zipcode-input");
            await zipInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            await zipInput.ClickAsync();
            await zipInput.FillAsync("");
            await zipInput.PressSequentiallyAsync(lead.ZipCode, new LocatorPressSequentiallyOptions { Delay = 120 });

            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
            }
            catch { }

            await Task.Delay(1500);
            var next1 = page.Locator("#next1");
            for (int i = 0; i < 20; i++)
            {
                var disabled = await next1.GetAttributeAsync("disabled");
                if (disabled == null) break;
                await Task.Delay(250);
            }
            await next1.ClickAsync(new LocatorClickOptions { Force = true });
            await Task.Delay(1000);

            var step2 = page.Locator("#step2");
            await step2.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            var noEdcMessage = step2.Locator(".error-message");
            var edcRadios = page.Locator("#step2 .inner-wrap:visible input[type='radio']");
            bool radiosFound = false;

            for (int i = 0; i < 40; i++)
            {
                if (await edcRadios.CountAsync() > 0)
                {
                    radiosFound = true;
                    break;
                }
                await Task.Delay(500);
            }

            if (!radiosFound)
            {
                if (await noEdcMessage.IsVisibleAsync())
                {
                    Logger.Log($"  No Electric Distribution Company found for {lead.ZipCode}");
                    csvRows.Add($"\"\",\"{lead.ZipCode}\",\"\",\"\",\"\",\"No offers available\"");
                    success = true;
                    break;
                }
                throw new Exception("Neither EDC radios nor 'no EDC' message appeared on step 2 after full wait.");
            }

            var allEdcRadios = await edcRadios.AllAsync();
            IElementHandle? matchingEdcRadio = null;

            // CHANGE: Find the radio button matching our specific Lead Utility Provider name
            foreach (var radio in allEdcRadios)
            {
                var id = await radio.GetAttributeAsync("id");
                if (id == null) continue;
                var labelLoc = page.Locator($"label[for='{id}']");
                if (await labelLoc.CountAsync() == 0) continue;
                var labelText = await labelLoc.InnerTextAsync();

                // Flexible match logic against the utility name provided by your lead API
                if (labelText.Contains(lead.UtilityProvider, StringComparison.OrdinalIgnoreCase) ||
                    lead.UtilityProvider.Contains(labelText, StringComparison.OrdinalIgnoreCase))
                {
                    matchingEdcRadio = await radio.ElementHandleAsync();
                    break;
                }
            }

            if (matchingEdcRadio == null)
            {
                Logger.Log($"  Target utility '{lead.UtilityProvider}' not found on the page options for zip {lead.ZipCode}.");
                success = true; // Break attempt loop, nothing matching to select
                break;
            }

            await matchingEdcRadio.AsElement()!.CheckAsync(new ElementHandleCheckOptions { Force = true });
            Logger.Log($"  Selected EDC: {lead.UtilityProvider}");

            var next2 = page.Locator("#next2");
            await next2.ClickAsync();

            var step3 = page.Locator("#step3");
            await step3.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

            var rateRadios = page.Locator("#step3 .inner-wrap:visible input[type='radio']");
            bool rateRadiosFound = false;
            for (int i = 0; i < 30; i++)
            {
                if (await rateRadios.CountAsync() > 0)
                {
                    rateRadiosFound = true;
                    break;
                }
                await Task.Delay(500);
            }

            if (!rateRadiosFound)
            {
                throw new Exception("Rate schedule radios never appeared on step 3.");
            }

            var allRateRadios = await rateRadios.AllAsync();
            IElementHandle? matchingRateRadio = null;
            string matchingRateLabel = "";

            foreach (var opt in allRateRadios)
            {
                var id = await opt.GetAttributeAsync("id");
                if (id == null) continue;
                var labelLoc = page.Locator($"label[for='{id}']");
                if (await labelLoc.CountAsync() == 0) continue;
                var labelText = await labelLoc.InnerTextAsync();
                if (labelText.Contains(targetRate, StringComparison.OrdinalIgnoreCase))
                {
                    matchingRateRadio = await opt.ElementHandleAsync();
                    matchingRateLabel = labelText;
                    break;
                }
            }

            if (matchingRateRadio == null)
            {
                Logger.Log($"  '{targetRate}' not offered for {lead.ZipCode} / {lead.UtilityProvider} - skipping");
                success = true;
                break;
            }

            await matchingRateRadio.AsElement()!.CheckAsync(new ElementHandleCheckOptions { Force = true });
            Logger.Log($"  Selected rate schedule: {matchingRateLabel}");

            var next3 = page.Locator("#next3");
            await next3.ClickAsync();

            await page.WaitForURLAsync("**/shop-for-rates-results/**", new PageWaitForURLOptions { Timeout = 30000 });
            bool hasDistCard;
            try
            {
                await page.WaitForSelectorAsync(".card.dist-card", new PageWaitForSelectorOptions
                {
                    Timeout = 20000,
                    State = WaitForSelectorState.Attached
                });
                hasDistCard = true;
            }
            catch (TimeoutException)
            {
                hasDistCard = false;
            }

            if (hasDistCard)
            {
                await ScrapeAndSavePriceAsync(page, lead.ZipCode, csvRows, apiHttpClient);
            }
            else
            {
                Logger.Log($"  No 'Current Price to Compare' block rendered for {lead.ZipCode} / {lead.UtilityProvider}");
            }

            success = true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed : {lead.ZipCode}, {lead.UtilityProvider} (attempt {attempt})");
            Logger.Log(ex.Message);
            if (attempt == 2)
            {
                try
                {
                    string screenshotFolder = Path.Combine(Logger.LogFolder, "Screenshots");
                    Directory.CreateDirectory(screenshotFolder);
                    string screenshotPath = Path.Combine(screenshotFolder, $"Error_{lead.ZipCode}_{lead.UtilityProvider.Replace(" ", "_")}.png");
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = screenshotPath,
                        FullPage = true
                    });
                    Logger.Log($"  Saved failure screenshot to {screenshotPath}");
                }
                catch { }
            }
            else
            {
                await Task.Delay(2000);
            }
        }
    }
    await context.CloseAsync();
}


async Task ScrapeAndSavePriceAsync(IPage page, string zip, ConcurrentBag<string> csvRows, HttpClient apiHttpClient)
{
    try
    {
        var infoDiv = page.Locator("#shop-for-rates").First;
        var utility = await infoDiv.GetAttributeAsync("data-edc") ?? "";
        var rateSchedule = await infoDiv.GetAttributeAsync("data-rs") ?? "";
        var distCard = page.Locator(".card.dist-card").First;
        if (string.IsNullOrEmpty(utility))
        {
            try
            {
                var fallback = await distCard.Locator(".company-info.dist .name").First.TextContentAsync();
                utility = (fallback ?? "").Trim();
            }
            catch { }
        }
        if (string.IsNullOrEmpty(rateSchedule))
        {
            try
            {
                var fallback = await distCard.Locator(".company-info.dist .rate-schedule").First.TextContentAsync();
                rateSchedule = (fallback ?? "").Trim();
            }
            catch { }
        }
        string perKwhText = "";
        try
        {
            var perKwhBlock = distCard.Locator(".data-wrap.number")
                .Filter(new LocatorFilterOptions { HasText = "per kwh" })
                .First;
            if (await perKwhBlock.CountAsync() > 0)
            {
                var text = await perKwhBlock.Locator(".highlight.large").First.TextContentAsync();
                perKwhText = (text ?? "").Replace("$", "").Trim();
            }
        }
        catch { }
        string estimatedMonthlyText = "";
        try
        {
            var monthlyBlock = distCard.Locator(".data-wrap.number")
                .Filter(new LocatorFilterOptions { HasText = "estimated monthly bill" })
                .First;
            if (await monthlyBlock.CountAsync() > 0)
            {
                var text = await monthlyBlock.Locator(".highlight.large").First.TextContentAsync();
                estimatedMonthlyText = (text ?? "").Replace("$", "").Trim();
            }
        }
        catch { }
        string lastUpdatedText = "";
        try
        {
            var lastUpdatedContent = await distCard.Locator(".last-updated").First.TextContentAsync();
            lastUpdatedText = (lastUpdatedContent ?? "").Replace("Rate last updated on", "").Trim();
        }
        catch { }
        csvRows.Add($"\"{utility}\",\"{zip}\",\"{rateSchedule}\",\"{perKwhText}\",\"{estimatedMonthlyText}\",\"{lastUpdatedText}\"");
        decimal.TryParse(perKwhText, out decimal pricePerKwh);
        decimal.TryParse(estimatedMonthlyText, out decimal estimatedMonthlyBill);
        DateTime lastUpdated;
        if (DateTime.TryParse(lastUpdatedText, out DateTime parsedDate))
        {
            lastUpdated = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
        }
        else
        {
            lastUpdated = DateTime.UtcNow;
        }
        var pricingPayload = new PtcPricingRequest
        {
            Utility = utility,
            ZipCode = zip,
            ServiceType = rateSchedule,
            PricePerKwh = pricePerKwh,
            EstimatedMonthlyBill = estimatedMonthlyBill,
            LastUpdated = lastUpdated
        };
        var postResponse = await apiHttpClient.PostAsJsonAsync("api/PTCPricingProcess/pricing-result", pricingPayload);
        if (postResponse.IsSuccessStatusCode)
        {
            Logger.Log($"  Successfully saved pricing to database for Zip: {zip}, Utility: {utility}");
        }
        else
        {
            var errorContent = await postResponse.Content.ReadAsStringAsync();
            Logger.Log($"  Failed to save pricing to database via API for Zip: {zip}. Error: {errorContent}");
        }
    }
    catch (Exception ex)
    {
        Logger.Log($"  Failed to scrape or save price data for {zip}: {ex.Message}");
    }
}

public class CustomerLeadDto
{
    public string ZipCode { get; set; } = string.Empty;
    public string UtilityProvider { get; set; } = string.Empty;
}

public class PtcPricingRequest
{
    public string Utility { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public decimal PricePerKwh { get; set; }
    public decimal EstimatedMonthlyBill { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Simple thread-safe logger that writes to both the console and a timestamped
/// log file inside a local "Logs" folder. Safe to call concurrently from the
/// parallel zip-processing tasks.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string _logFilePath = "";
    public static string LogFolder { get; private set; } = "";

    public static void Init(string folder)
    {
        LogFolder = folder;
        Directory.CreateDirectory(LogFolder);
        _logFilePath = Path.Combine(LogFolder, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
    }
}