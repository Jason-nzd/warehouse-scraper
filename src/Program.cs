using System.Diagnostics;
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
        // secondsDelayBetweenPageScrapes - use a reasonable value to prevent overloading the server
        static int secondsDelayBetweenPageScrapes = 11;

        // uploadProductImages - will send product images to an Azure Function for processing
        static bool uploadProductImages = true;

        public record Product(
            string id,
            string name,
            string? size,
            float currentPrice,
            string[] category,
            string sourceSite,
            DatedPrice[] priceHistory,
            DateTime lastUpdated,
            DateTime lastChecked,
            float? unitPrice,
            string? unitName,
            float? originalUnitQuantity
        );
        public record DatedPrice(DateTime date, float price);

        // Singletons for Playwright
        public static IPlaywright? playwright;
        public static IBrowser? browser;
        public static IPage? playwrightPage;
        public static HttpClient httpclient = new HttpClient();

        // Static variables which are set as command line arguments
        private static bool dryRunMode = false;
        private static bool reverseMode = false;

        // Get CosmosDB config entries from appsettings.json
        public static IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        public static async Task Main(string[] args)
        {
            // Handle arguments - 'dotnet run dry' will run in dry mode, bypassing any CosmosDB writes
            // 'dotnet run reverse' will reverse the order that each page is loaded
            if (args.Length > 0)
            {
                if (args.Contains("dry"))
                {
                    dryRunMode = true;
                    Log(ConsoleColor.Yellow, $"\n(Dry Run mode on)");
                }
                if (args.Contains("reverse")) reverseMode = true;
            }

            // Start stopwatch for recording time elapsed
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

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

            // Parse and optimise each line into valid urls
            List<CategorisedURL> categorisedUrls = new List<CategorisedURL>();
            foreach (string line in lines)
            {
                CategorisedURL? categorisedURL =
                    ParseLineToCategorisedURL(
                        line,
                        urlShouldContain: "warehouse.co.nz",
                        replaceQueryParamsWith: "prefn1=marketplaceItem&prefv1=The Warehouse&srule=best-sellers&sz=64"
                    );

                if (categorisedURL != null) categorisedUrls.Add((CategorisedURL)categorisedURL);
            }

            Log(ConsoleColor.Yellow,
                $"{categorisedUrls.Count} pages to be scraped, with {secondsDelayBetweenPageScrapes}s delay between page scrape."
            );

            // Conditionally reverse the order of categorisedUrls
            if (reverseMode) categorisedUrls.Reverse();

            // Open up each URL and run the scraping function
            for (int i = 0; i < categorisedUrls.Count(); i++)
            {
                try
                {
                    // Separate out url from categorisedUrl
                    string url = categorisedUrls[i].url;

                    // Log current sequence of page scrapes, the total num of pages to scrape, and shortened url
                    string shortenedUrl = categorisedUrls[i].url.Replace("https://www.", "");

                    Log(ConsoleColor.Yellow,
                        $"\nLoading Page [{i + 1}/{categorisedUrls.Count()}] {shortenedUrl}");

                    // Try load page and wait for full content to dynamically load in
                    await playwrightPage!.GotoAsync(url);
                    await playwrightPage.WaitForSelectorAsync("div.price-lockup-wrapper");

                    // Check page title for page not found
                    var titleElement = await playwrightPage.QuerySelectorAllAsync("div.page-title h1.title");
                    string title = await titleElement.First().InnerTextAsync();
                    if (title.Contains("Oops! We can’t find that page")) throw new PlaywrightException("404 Page Not Found");

                    // Query all product card entries, and log how many were found
                    var productElements = await playwrightPage.QuerySelectorAllAsync("div.product-tile");
                    Log(ConsoleColor.Yellow,
                        $"{productElements.Count} products found \t" +
                        $"Total Time Elapsed: {stopwatch.Elapsed.Minutes}:{stopwatch.Elapsed.Seconds.ToString().PadLeft(2, '0')}\t" +
                        $"Categories: {String.Join(", ", categorisedUrls[i].categories)}");

                    // Create counters for logging purposes
                    int newCount = 0, priceUpdatedCount = 0, nonPriceUpdatedCount = 0, upToDateCount = 0;

                    // Loop through each found playwright element
                    foreach (var productElement in productElements)
                    {
                        // Create Product object from playwright element
                        Product? scrapedProduct = await PlaywrightElementToProduct(
                            productElement,
                            url,
                            categorisedUrls[i].categories
                        );

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

                            if (uploadProductImages)
                            {
                                // Use Azure Function to upload product image
                                string hiResImageUrl = await GetHiresImageUrl(productElement);
                                if (hiResImageUrl != "" && hiResImageUrl != null)
                                    await UploadImageUsingRestAPI(hiResImageUrl, scrapedProduct);
                            }
                        }
                        else if (dryRunMode && scrapedProduct != null)
                        {
                            // In Dry Run mode, print a formatted row for each product
                            string unitString = scrapedProduct.unitPrice != null ?
                                " | $" + scrapedProduct.unitPrice + " /" + scrapedProduct.unitName : "";

                            Console.WriteLine(
                                scrapedProduct!.id.PadLeft(9) + " | " +
                                scrapedProduct.name!.PadRight(60).Substring(0, 60) + " | " +
                                scrapedProduct.size!.PadRight(8) + " | $" +
                                scrapedProduct.currentPrice.ToString().PadLeft(5) + unitString
                            );
                        }
                    }

                    if (!dryRunMode)
                    {
                        // Log consolidated CosmosDB stats for entire page scrape
                        Log(ConsoleColor.Cyan, $"CosmosDB: {newCount} new products, " +
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
                if (i != categorisedUrls.Count() - 1)
                {
                    Thread.Sleep(secondsDelayBetweenPageScrapes * 1000);
                }
            }

            // After all pages have been scraped,
            // clean up playwright browser and other resources, then end program
            try
            {
                Log(ConsoleColor.White, "Scraping Completed \n");
                await playwrightPage!.Context.CloseAsync();
                await playwrightPage.CloseAsync();
                await browser!.CloseAsync();
            }
            catch (Exception)
            {
            }
            return;
        }

        // EstablishPlaywright()
        private async static Task EstablishPlaywright()
        {
            try
            {
                // Launch chromium browser in headless mode
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
            catch (PlaywrightException)
            {
                Log(
                    ConsoleColor.Red,
                    "Browser must be manually installed using: \n" +
                    "pwsh bin/Debug/net6.0/playwright.ps1 install\n"
                );
                throw;
            }
        }

        // GetHiresImageUrl()
        // ------------------
        // Get the hi-res image url from a Playwright element
        public async static Task<string> GetHiresImageUrl(IElementHandle productElement)
        {
            var imgDiv = await productElement.QuerySelectorAsync(".tile-image");
            string? imgUrl = await imgDiv!.GetAttributeAsync("src");

            // Check if image is a valid product image
            if (!imgUrl!.Contains("sw=292&sh=292")) return "";

            // Swap url params to get hi-res version
            return imgUrl!.Replace("sw=292&sh=292", "sw=765&sh=765");
        }


        // PlaywrightElementToProduct()
        // ----------------------------
        // Takes a playwright element "div.product-tile", scrapes each of the desired data fields,
        // Returns a completed Product record, or null if invalid
        private async static Task<Product?> PlaywrightElementToProduct(IElementHandle productElement, string url, string[] categories)
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
                var priceTag = await productElement.QuerySelectorAsync("gep-price");
                var priceString = await priceTag!.GetAttributeAsync("value");
                float currentPrice = float.Parse(priceString!.Substring(1));

                // Source Website
                string sourceSite = "thewarehouse.co.nz";

                // Size
                string size = ExtractProductSize(name);

                // Determine if an in-store only product
                var availabilityElement = await productElement.QuerySelectorAsync("div.availability-stock-status");
                var availabilityTag = await availabilityElement!.GetAttributeAsync("data-stock-status");
                bool isInStoreOnly = availabilityTag == "FIND_IN_STORE";

                // Determine if a clearance product
                bool isClearance = false;
                var clearanceImgElement = await productElement.QuerySelectorAsync("img.product-badge-image");
                if (clearanceImgElement != null)
                {
                    var clearanceTag = await clearanceImgElement.GetAttributeAsync("src");
                    isClearance = clearanceTag!.Contains("clearanceUpdate.svg");
                }

                // Reject hard to find products marked as both clearance and in-store only
                if (isInStoreOnly && isClearance)
                {
                    Log(ConsoleColor.Gray, $"  Ignoring - {name} - (In-store only and clearance product)");
                    return null;
                }

                // Check for manual product data overrides
                SizeAndCategoryOverride overrides = CheckProductOverrides(id);
                if (overrides.size != "") size = overrides.size;
                if (overrides.category != "") categories = new string[] { overrides.category };

                // Create a DateTime object for the current time, but set minutes and seconds to zero
                DateTime todaysDate = DateTime.UtcNow;
                todaysDate = new DateTime(
                    todaysDate.Year,
                    todaysDate.Month,
                    todaysDate.Day,
                    todaysDate.Hour,
                    0,
                    0
                );

                // Create a DatedPrice for the current time and price
                DatedPrice todaysDatedPrice = new DatedPrice(todaysDate, currentPrice);

                // Create Price History array with a single element
                DatedPrice[] priceHistory = new DatedPrice[] { todaysDatedPrice };

                // Get derived unit price, unit name, original unit quantity
                string? unitPriceString = DeriveUnitPriceString(size, currentPrice);
                float? unitPrice = null;
                string? unitName = "";
                float? originalUnitQuantity = null;
                if (unitPriceString != null)
                {
                    unitPrice = float.Parse(unitPriceString.Split("/")[0]);
                    unitName = unitPriceString.Split("/")[1];
                    originalUnitQuantity = float.Parse(unitPriceString.Split("/")[2]);
                }

                // Create product record with above values
                Product product = new Product(
                    id,
                    name!,
                    size,
                    currentPrice,
                    categories,
                    sourceSite,
                    priceHistory,
                    todaysDate,
                    todaysDate,
                    unitPrice,
                    unitName,
                    originalUnitQuantity
                );

                // Validate then return completed product
                if (IsValidProduct(product)) return product;
                else throw new Exception(product.name);
            }
            catch (Exception e)
            {
                Log(ConsoleColor.Red, $"Price scrape error: " + e.Message);
                // Return null if any exceptions occurred during scraping
                return null;
            }
        }

        // RoutePlaywrightExclusions()
        // ---------------------------
        // Routes all requests and aborts any that match the defined exclusions.
        // Exclusions include ads, trackers, and bandwidth heavy images

        private static async Task RoutePlaywrightExclusions(bool logToConsole)
        {
            // Define excluded types and urls to reject
            // Define unnecessary types and ad/tracking urls to reject
            string[] typeExclusions = { "image", "stylesheet", "media", "font", "other" };
            List<string> exclusions = new List<string>()
            {
                "googleoptimize.com", "gtm.js", "visitoridentification.js","js-agent.newrelic.com",
                "cquotient.com", "googletagmanager.com", "cloudflareinsights.com", "dwanalytics", "edge.adobedc.net"
            };

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
    }
}