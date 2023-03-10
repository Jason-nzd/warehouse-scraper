using Microsoft.Playwright;
using Microsoft.Extensions.Configuration;
using static Scraper.CosmosDB;
using static Scraper.Utilities;

// Warehouse Scraper
// Scrapes product info and pricing from The Warehouse NZ's website.

namespace Scraper
{
    public class Program
    {
        static int secondsDelayBetweenPageScrapes = 15;
        static bool uploadImagesToAzureFunc = true;

        public record Product(
            string id,
            string name,
            string size,
            float currentPrice,
            string[] category,
            string sourceSite,
            DatedPrice[] priceHistory,
            DateTime lastUpdated
        );
        public record DatedPrice(DateTime date, float price);

        // Singletons for Playwright
        public static IPlaywright? playwright;
        public static IBrowser? browser;
        public static IPage? playwrightPage;
        public static HttpClient httpclient = new HttpClient();

        // Get config from appsettings.json
        public static IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        public static async Task Main(string[] args)
        {
            // Handle arguments - 'dotnet run dry' will run in dry mode, bypassing CosmosDB
            //  'dotnet run reverse' will reverse the order that each page is loaded
            if (args.Length > 0)
            {
                if (args.Contains("dry")) dryRunMode = true;
                if (args.Contains("reverse")) reverseMode = true;
                Log(ConsoleColor.Yellow, $"\n(Dry Run mode on)");
            }

            // Establish Playwright browser
            await EstablishPlaywright();

            // Connect to CosmosDB - end program if unable to connect
            if (!dryRunMode)
            {
                if (!await CosmosDB.EstablishConnection(
                       db: "supermarket-prices",
                       partitionKey: "/name",
                       container: "products"
                   )) return;
            }

            // Read lines from text file - end program if unable to read
            List<string>? lines = ReadLinesFromFile("Urls.txt");
            if (lines == null) return;

            // Parse and optimise each line into valid urls to be scraped
            List<string> urls = new List<string>();
            foreach (string line in lines)
            {
                string? validUrl =
                    ParseAndOptimiseURL(
                        url: line,
                        urlShouldContain: "warehouse.co.nz",
                        replaceQueryParams: "?prefn1=marketplaceItem&prefv1=The Warehouse&srule=best-sellers"
                    );
                if (validUrl != null) urls.Add(validUrl);
            }

            // Optionally reverse the order of urls
            if (reverseMode) urls.Reverse();

            // Open up each URL and run the scraping function
            for (int i = 0; i < urls.Count(); i++)
            {
                try
                {
                    // Try load page and wait for full content to dynamically load in
                    Log(ConsoleColor.Yellow,
                        $"\nLoading Page [{i + 1}/{urls.Count()}] {urls[i].PadRight(112).Substring(12, 100)}");

                    await playwrightPage!.GotoAsync(urls[i]);
                    await playwrightPage.WaitForSelectorAsync("div.price-lockup-wrapper");

                    // Query all product card entries
                    var productElements = await playwrightPage.QuerySelectorAllAsync("div.product-tile");
                    Log(ConsoleColor.Yellow, productElements.Count + " products found");

                    // Create counters for logging purposes
                    int newCount = 0, priceUpdatedCount = 0, nonPriceUpdatedCount = 0, upToDateCount = 0;

                    // Loop through every found playwright element
                    foreach (var productElement in productElements)
                    {
                        // Create Product object from playwright element
                        Product? scrapedProduct = await ScrapeProductElementToRecord(productElement, urls[i]);

                        if (!dryRunMode && scrapedProduct != null)
                        {
                            // Try upsert to CosmosDB
                            UpsertResponse response = await CosmosDB.UpsertProduct(scrapedProduct);

                            // Increment stats counters based on response from CosmosDB
                            switch (response)
                            {
                                case UpsertResponse.NewProduct:
                                    newCount++;
                                    break;
                                case UpsertResponse.PriceUpdated:
                                    priceUpdatedCount++;
                                    break;
                                case UpsertResponse.NonPriceUpdated:
                                    nonPriceUpdatedCount++;
                                    break;
                                case UpsertResponse.AlreadyUpToDate:
                                    upToDateCount++;
                                    break;
                                case UpsertResponse.Failed:
                                default:
                                    break;
                            }

                            if (uploadImagesToAzureFunc)
                            {
                                // Use Azure Function to upload product image
                                string hiResImageUrl = await GetHiresImageUrl(productElement);
                                if (hiResImageUrl != "" || hiResImageUrl != null)
                                    await UploadImageUsingRestAPI(hiResImageUrl, scrapedProduct);
                            }
                        }
                        else if (dryRunMode && scrapedProduct != null)
                        {
                            // In Dry Run mode, print a log row for every product
                            Console.WriteLine(
                                scrapedProduct!.id.PadLeft(9) + " | " +
                                scrapedProduct.name!.PadRight(40).Substring(0, 40) + " | " +
                                scrapedProduct.size.PadRight(8) + " | $" +
                                scrapedProduct.currentPrice.ToString().PadLeft(5) + " | " +
                                scrapedProduct.category[0]
                            );
                        }
                    }

                    if (!dryRunMode)
                    {
                        // Log consolidated CosmosDB stats for entire page scrape
                        Log(ConsoleColor.Blue, $"\nCosmosDB: {newCount} new products, " +
                        $"{priceUpdatedCount} prices updated, {nonPriceUpdatedCount} info updated, " +
                        $"{upToDateCount} already up-to-date");
                    }
                }
                catch (TimeoutException)
                {
                    LogError("Unable to Load Web Page - timed out after 30 seconds");
                }
                catch (PlaywrightException e)
                {
                    LogError("Unable to Load Web Page - " + e.Message);
                }
                catch (Exception e)
                {
                    Console.Write(e.ToString());
                    return;
                }

                // This page has now completed scraping. A delay is added in-between each subsequent URL
                if (i != urls.Count() - 1)
                {
                    Log(ConsoleColor.Gray,
                        $"Waiting {secondsDelayBetweenPageScrapes}s until next page scrape.."
                    );
                    Thread.Sleep(secondsDelayBetweenPageScrapes * 1000);
                }
            }

            // Try clean up playwright browser and other resources, then end program
            try
            {
                Log(ConsoleColor.Blue, "\nScraping Completed \n");
                await playwrightPage!.Context.CloseAsync();
                await playwrightPage.CloseAsync();
                await browser!.CloseAsync();
            }
            catch (Exception)
            {
            }
            return;
        }

        private async static Task EstablishPlaywright()
        {
            try
            {
                // Launch Playwright Browser in headless mode
                playwright = await Playwright.CreateAsync();

                browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true }
                );

                // Launch Page 
                playwrightPage = await browser.NewPageAsync();

                // Route exclusions, such as ads, trackers, etc
                await RoutePlaywrightExclusions(false);
                return;
            }
            catch (Microsoft.Playwright.PlaywrightException)
            {
                Log(
                    ConsoleColor.Red,
                    "Browser must be manually installed using: \n" +
                    "pwsh bin/Debug/net6.0/playwright.ps1 install\n"
                );
                throw;
            }
        }

        // Get the hi-res image url from the Playwright element
        public async static Task<string> GetHiresImageUrl(IElementHandle productElement)
        {
            var imgDiv = await productElement.QuerySelectorAsync(".tile-image");
            string? imgUrl = await imgDiv!.GetAttributeAsync("src");

            // Check if image is a valid product image
            if (!imgUrl!.Contains("sw=292&sh=292")) return "";

            // Swap url params to get hi-res version
            return imgUrl!.Replace("sw=292&sh=292", "sw=765&sh=765");
        }



        // Takes a playwright element "div.product-tile", scrapes each of the desired data fields,
        //  and then returns a completed Product record
        private async static Task<Product?> ScrapeProductElementToRecord(IElementHandle productElement, string url)
        {
            try
            {
                // Name
                var aTag = await productElement.QuerySelectorAsync("a.link");
                string? name = await aTag!.InnerTextAsync();

                // ID
                var linkHref = await aTag.GetAttributeAsync("href");   // get href to product page
                var fileName = linkHref!.Split('/').Last();            // get filename ending in .html
                string id = fileName.Split('.').First();               // extract ID from filename

                // Price
                var priceTag = await productElement.QuerySelectorAsync("span.now-price");
                var priceString = await priceTag!.InnerTextAsync();
                float currentPrice = float.Parse(priceString.Substring(1));

                // Source Website
                string sourceSite = "thewarehouse.co.nz";

                // Categories
                string[]? categories = new string[] { DeriveCategoryFromUrl(url, "/food-drink/") };

                // Size
                string size = ExtractProductSize(name);

                // DatedPrice
                DateTime todaysDate = DateTime.UtcNow;
                DatedPrice todaysDatedPrice = new DatedPrice(todaysDate, currentPrice);

                // Create Price History array with a single element
                DatedPrice[] priceHistory = new DatedPrice[] { todaysDatedPrice };

                // Return completed Product record
                return new Product(
                    id,
                    name!,
                    size,
                    currentPrice,
                    categories!,
                    sourceSite,
                    priceHistory,
                    todaysDate
                );
            }
            catch (Exception e)
            {
                Log(ConsoleColor.Red, $"Price scrape error: " + e.Message);
                // Return null if any exceptions occurred during scraping
                return null;
            }
        }

        private static async Task RoutePlaywrightExclusions(bool logToConsole)
        {
            // Define excluded types and urls to reject
            // Define unnecessary types and ad/tracking urls to reject
            string[] typeExclusions = { "image", "stylesheet", "media", "font", "other" };
            string[] urlExclusions = { "googleoptimize.com", "gtm.js", "visitoridentification.js", "js-agent.newrelic.com",
            "cquotient.com", "googletagmanager.com", "cloudflareinsights.com", "dwanalytics", "edge.adobedc.net" };
            List<string> exclusions = urlExclusions.ToList();

            // Route with exclusions processed
            await playwrightPage!.RouteAsync("**/*", async route =>
            {
                var req = route.Request;
                bool excludeThisRequest = false;
                string trimmedUrl = req.Url.Length > 120 ? req.Url.Substring(0, 120) + "..." : req.Url;

                foreach (string exclusion in exclusions)
                {
                    if (req.Url.Contains(exclusion)) excludeThisRequest = true;
                }
                if (typeExclusions.Contains(req.ResourceType)) excludeThisRequest = true;

                if (excludeThisRequest)
                {
                    if (logToConsole) LogError($"{req.Method} {req.ResourceType} - {trimmedUrl}");
                    await route.AbortAsync();
                }
                else
                {
                    if (logToConsole) Log(ConsoleColor.White, $"{req.Method} {req.ResourceType} - {trimmedUrl}");
                    await route.ContinueAsync();
                }
            });
        }

        private static bool dryRunMode = false;
        private static bool reverseMode = false;
    }
}