using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Text;

var zipCodes = new[]
{
    "17503", "17504", "17505", "17506", "17507",
    "17508", "17509", "17512", "17516", "17517",
    "19188", "19191", "19192", "19193", "19194",
    "19195", "15313", "15314", "15317", "15321",
    "15322", "15323", "15324", "15329", "15330"
};

const string TargetRateSchedule = "Regular Residential Service";
const string HomeUrl = "https://www.papowerswitch.com/";

// ---- Concurrency tuning -----------------------------------------------
// How many zip codes are processed at the same time. Each "slot" is an
// isolated BrowserContext + Page inside a single shared Chromium process.
// Start around 4-6. Push higher only if the site tolerates it and your
// machine has the CPU/RAM to spare; too high risks throttling/blocking
// or flaky selectors due to contention.
const int MaxConcurrency = 5;
// -------------------------------------------------------------------------

// Thread-safe collector for CSV rows (StringBuilder is NOT thread-safe).
var csvRows = new ConcurrentBag<string>();

try
{
    using var playwright = await Playwright.CreateAsync();

    // One shared browser process; contexts give us per-zip isolation
    // (separate cookies/localStorage/cache) without the overhead of
    // spinning up N full Chromium instances.
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true // headless is strongly recommended once running in parallel
    });

    using var throttle = new SemaphoreSlim(MaxConcurrency);

    var tasks = zipCodes.Select(async zip =>
    {
        await throttle.WaitAsync();
        try
        {
            await ProcessZipAsync(browser, zip, csvRows, TargetRateSchedule);
        }
        catch (Exception zipEx)
        {
            Console.WriteLine($"Unhandled error while processing zip {zip}: {zipEx.Message}");
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
    Console.WriteLine($"Fatal error during scraping run: {ex.Message}");
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
        Console.WriteLine("Completed.");
        Console.WriteLine("CSV saved as PowerRates.csv");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to write CSV file: {ex.Message}");
    }
}

async Task ProcessZipAsync(IBrowser browser, string zip, ConcurrentBag<string> csvRows, string targetRateSchedule)
{
    // Each zip gets its own context + page so parallel runs don't share
    // cookies/session state and don't fight over the same page instance.
    await using var context = await browser.NewContextAsync();
    var page = await context.NewPageAsync();

    var distributorIndex = 0;
    var totalDistributors = 1;
    var zipHadAnyOffers = false;

    while (distributorIndex < totalDistributors)
    {
        var success = false;

        for (int attempt = 1; attempt <= 2 && !success; attempt++)
        {
            try
            {
                Console.WriteLine($"Processing {zip}, EDC #{distributorIndex + 1} (attempt {attempt})");

                await page.GotoAsync(HomeUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Load,
                    Timeout = 30000
                });

                var zipInput = page.Locator("#zipcode-input");
                await zipInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
                await zipInput.ClickAsync();
                await zipInput.FillAsync("");
                await zipInput.PressSequentiallyAsync(zip, new LocatorPressSequentiallyOptions { Delay = 120 });

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                }
                catch
                {
                }

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
                    var radioCount = await edcRadios.CountAsync();
                    if (radioCount > 0)
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
                        Console.WriteLine($"  No Electric Distribution Company found for {zip}");
                        csvRows.Add($"\"\",\"{zip}\",\"\",\"\",\"\",\"No offers available\"");
                        success = true;
                        totalDistributors = 0;
                        break;
                    }

                    throw new Exception("Neither EDC radios nor 'no EDC' message appeared on step 2 after full wait.");
                }

                var allEdcRadios = await edcRadios.AllAsync();
                totalDistributors = allEdcRadios.Count;

                if (distributorIndex >= totalDistributors)
                {
                    success = true;
                    break;
                }

                var chosenEdcRadio = allEdcRadios[distributorIndex];
                var edcId = await chosenEdcRadio.GetAttributeAsync("id");
                string edcLabel = "";
                if (edcId != null)
                {
                    var labelLoc = page.Locator($"label[for='{edcId}']");
                    if (await labelLoc.CountAsync() > 0)
                        edcLabel = await labelLoc.InnerTextAsync();
                }

                await chosenEdcRadio.CheckAsync(new LocatorCheckOptions { Force = true });

                Console.WriteLine($"  Selected EDC: {(string.IsNullOrWhiteSpace(edcLabel) ? "(unlabeled)" : edcLabel)} ({distributorIndex + 1}/{totalDistributors})");

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
                    if (labelText.Contains(targetRateSchedule, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingRateRadio = await opt.ElementHandleAsync();
                        matchingRateLabel = labelText;
                        break;
                    }
                }

                if (matchingRateRadio == null)
                {
                    Console.WriteLine($"  '{targetRateSchedule}' not offered for {zip} / {edcLabel} - skipping this EDC");
                    success = true;
                    distributorIndex++;
                    continue;
                }

                await matchingRateRadio.AsElement()!.CheckAsync(new ElementHandleCheckOptions { Force = true });
                Console.WriteLine($"  Selected rate schedule: {matchingRateLabel}");

                var next3 = page.Locator("#next3");
                await next3.ClickAsync();

                await page.WaitForURLAsync("**/shop-for-rates-results/**", new PageWaitForURLOptions { Timeout = 20000 });

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
                    await ScrapeCurrentPriceToCompareAsync(page, zip, csvRows);
                    zipHadAnyOffers = true;
                }
                else
                {
                    Console.WriteLine($"  No 'Current Price to Compare' block rendered for {zip} / {edcLabel}");
                }

                success = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed : {zip}, EDC #{distributorIndex + 1} (attempt {attempt})");
                Console.WriteLine(ex.Message);

                if (attempt == 2)
                {
                    try
                    {
                        await page.ScreenshotAsync(new PageScreenshotOptions
                        {
                            Path = $"Error_{zip}_edc{distributorIndex + 1}.png",
                            FullPage = true
                        });
                    }
                    catch
                    {
                    }
                }
                else
                {
                    await Task.Delay(2000);
                }
            }
        }

        distributorIndex++;
    }

    if (!zipHadAnyOffers && totalDistributors > 0)
    {
        Console.WriteLine($"  Note: {zip} resolved to {totalDistributors} EDC(s) but no supplier offers were captured.");
    }

    await context.CloseAsync();
}

async Task ScrapeCurrentPriceToCompareAsync(IPage page, string zip, ConcurrentBag<string> csvRows)
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
            catch
            {
            }
        }

        if (string.IsNullOrEmpty(rateSchedule))
        {
            try
            {
                var fallback = await distCard.Locator(".company-info.dist .rate-schedule").First.TextContentAsync();
                rateSchedule = (fallback ?? "").Trim();
            }
            catch
            {
            }
        }

        string perKwh = "";
        try
        {
            var perKwhBlock = distCard.Locator(".data-wrap.number")
                .Filter(new LocatorFilterOptions { HasText = "per kwh" })
                .First;

            if (await perKwhBlock.CountAsync() > 0)
            {
                var text = await perKwhBlock.Locator(".highlight.large").First.TextContentAsync();
                perKwh = (text ?? "").Replace("$", "").Trim();
            }
        }
        catch
        {
        }

        string estimatedMonthly = "";
        try
        {
            var monthlyBlock = distCard.Locator(".data-wrap.number")
                .Filter(new LocatorFilterOptions { HasText = "estimated monthly bill" })
                .First;

            if (await monthlyBlock.CountAsync() > 0)
            {
                var text = await monthlyBlock.Locator(".highlight.large").First.TextContentAsync();
                estimatedMonthly = (text ?? "").Replace("$", "").Trim();
            }
        }
        catch
        {
        }

        string lastUpdated = "";
        try
        {
            var lastUpdatedText = await distCard.Locator(".last-updated").First.TextContentAsync();
            lastUpdated = (lastUpdatedText ?? "").Replace("Rate last updated on", "").Trim();
        }
        catch
        {
        }

        csvRows.Add($"\"{utility}\",\"{zip}\",\"{rateSchedule}\",\"{perKwh}\",\"{estimatedMonthly}\",\"{lastUpdated}\"");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Failed to scrape price data for {zip}: {ex.Message}");
    }
}