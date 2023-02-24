using Microsoft.Playwright;
using Microsoft.Azure.Cosmos;
using System.Text.RegularExpressions;

// Warehouse Scraper
// Scrapes product info and pricing from The Warehouse NZ's website.
// dryRunMode = true - will skip CosmosDB connections and only log to console

namespace WarehouseScraper
{
    public class Program
    {
        static int secondsDelayBetweenPageScrapes = 32;
        static string[] urls = new string[] {
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/milk",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/bread",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/cheese",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/eggs",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/chips-snacks/biscuits",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/chips-snacks/chips",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/chips-snacks/snack-muesli-bars"
        };

        public record Product(
            string id,
            string name,
            string size,
            float currentPrice,
            string[] category,
            string sourceSite,
            DatedPrice[] priceHistory
        );
        public record DatedPrice(string date, float price);

        public static CosmosClient? cosmosClient;
        public static Database? database;
        public static Container? cosmosContainer;
        public static IPage? playwrightPage;

        public static async Task Main(string[] args)
        {
            // Handle arguments - 'dotnet run dry' will run in dry mode, bypassing CosmosDB
            if (args.Length > 0)
            {
                if (args[0] == "dry") dryRunMode = true;
                Log(ConsoleColor.Yellow, $"\n(Dry Run mode on)");
            }

            // Launch Playwright Browser
            var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true }
            );
            playwrightPage = await browser.NewPageAsync();
            await RoutePlaywrightExclusions(logToConsole: false);

            // Connect to CosmosDB - end program if unable to connect
            if (!dryRunMode)
            {
                if (!await CosmosDB.EstablishConnection(
                       databaseName: "supermarket-prices",
                       partitionKey: "/name",
                       containerName: "products"
                   )) return;
            }

            // Clean URLs and add desired query options
            urls = CleanAndParseURLs(urls);

            // Open up each URL and run the scraping function
            for (int i = 0; i < urls.Count(); i++)
            {
                // Try load page and wait for full content to dynamically load in
                try
                {
                    Log(ConsoleColor.Yellow,
                        $"\nLoading Page [{i + 1}/{urls.Count()}] {urls[i].PadRight(112).Substring(12, 100)}");
                    await playwrightPage!.GotoAsync(urls[i]);
                    await playwrightPage.WaitForSelectorAsync("div.price-lockup-wrapper");
                }
                catch (System.Exception e)
                {
                    Log(ConsoleColor.Red, "Unable to Load Web Page");
                    Console.Write(e.ToString());
                    return;
                }

                // Query all product card entries
                var productElements = await playwrightPage.QuerySelectorAllAsync("div.product-tile");
                Log(ConsoleColor.Yellow, productElements.Count.ToString().PadLeft(8) + " products found");

                // Create counters for logging purposes
                int newProductsCount = 0, updatedProductsCount = 0, upToDateProductsCount = 0;

                // Loop through every found playwright element
                foreach (var element in productElements)
                {
                    // Create Product object from playwright element
                    Product scrapedProduct = await ScrapeProductElementToRecord(element, urls[i]);

                    if (!dryRunMode)
                    {
                        // Try upsert to CosmosDB
                        UpsertResponse response = await CosmosDB.UpsertProduct(scrapedProduct);

                        // Increment stats counters based on response from CosmosDB
                        switch (response)
                        {
                            case UpsertResponse.NewProduct:
                                newProductsCount++;
                                break;

                            case UpsertResponse.Updated:
                                updatedProductsCount++;
                                break;

                            case UpsertResponse.AlreadyUpToDate:
                                upToDateProductsCount++;
                                break;

                            case UpsertResponse.Failed:
                            default:
                                break;
                        }
                    }
                    else
                    {
                        // In Dry Run mode, print a log row for every product
                        Console.WriteLine(
                            scrapedProduct.id.PadLeft(9) + " | " +
                            scrapedProduct.name!.PadRight(50).Substring(0, 50) + " | " + scrapedProduct.size.PadRight(8) +
                            " | $" + scrapedProduct.currentPrice.ToString().PadLeft(5) + " | " + scrapedProduct.category.Last()
                        );
                    }
                }

                if (!dryRunMode)
                {
                    // Log consolidated CosmosDB stats for entire page scrape
                    Log(ConsoleColor.Blue, $"{"CosmosDB:".PadLeft(15)} {newProductsCount} new products, " +
                    $"{updatedProductsCount} updated, {upToDateProductsCount} already up-to-date");
                }

                // This page has now completed scraping. A delay is added in-between each subsequent URL
                if (i != urls.Count() - 1)
                {
                    Log(ConsoleColor.Gray,
                        $"{"Waiting".PadLeft(10)} {secondsDelayBetweenPageScrapes}s until next page scrape.."
                    );
                    Thread.Sleep(secondsDelayBetweenPageScrapes * 1000);
                }
            }

            // Clean up playwright browser and end program
            Log(ConsoleColor.Blue, "\nScraping Completed \n");
            await browser.CloseAsync();
            return;
        }

        async static Task openEachURLForScraping(string[] urls, IPage page)
        {
            int urlIndex = 1;

            foreach (var url in urls)
            {
                // Try load page and wait for full page to dynamically load in
                try
                {
                    Log(ConsoleColor.Yellow, $"\nLoading Page [{urlIndex++}/{urls.Count()}] {url.PadRight(112).Substring(12, 100)}");
                    await playwrightPage!.GotoAsync(url);
                    await playwrightPage.WaitForSelectorAsync("div.price-lockup-wrapper");
                }
                catch (System.Exception e)
                {
                    Log(ConsoleColor.Red, "Unable to Load Web Page");
                    Console.Write(e.ToString());
                    return;
                }

                // Query all product card entries
                var productElements = await playwrightPage.QuerySelectorAllAsync("div.product-tile");
                Log(ConsoleColor.Yellow, productElements.Count.ToString().PadLeft(12) + " products found");

                // Create counters for logging purposes
                int newProductsCount = 0, updatedProductsCount = 0, upToDateProductsCount = 0;

                // Loop through every found playwright element
                foreach (var element in productElements)
                {
                    // Create Product object from playwright element
                    Product scrapedProduct = await ScrapeProductElementToRecord(element, url);

                    if (!dryRunMode)
                    {
                        // Try upsert to CosmosDB
                        UpsertResponse response = await CosmosDB.UpsertProduct(scrapedProduct);

                        // Increment stats counters based on response from CosmosDB
                        switch (response)
                        {
                            case UpsertResponse.NewProduct:
                                newProductsCount++;
                                break;

                            case UpsertResponse.Updated:
                                updatedProductsCount++;
                                break;

                            case UpsertResponse.AlreadyUpToDate:
                                upToDateProductsCount++;
                                break;

                            case UpsertResponse.Failed:
                            default:
                                break;
                        }
                    }
                    else
                    {
                        // In Dry Run mode, print a log row for every product
                        Console.WriteLine(
                            scrapedProduct.id.PadLeft(9) + " | " +
                            scrapedProduct.name!.PadRight(50).Substring(0, 50) + " | " +
                            scrapedProduct.size.PadRight(10).Substring(0, 10) + " | " +
                            "$" + scrapedProduct.currentPrice + "\t| " + scrapedProduct.category.Last()
                        );
                    }
                }

                if (!dryRunMode)
                {
                    // Log consolidated CosmosDB stats for entire page scrape
                    Log(ConsoleColor.Blue, $"{"CosmosDB:".PadLeft(15)} {newProductsCount} new products, " +
                    $"{updatedProductsCount} updated, {upToDateProductsCount} already up-to-date");
                }

                // This page has now completed scraping. A delay is added in-between each subsequent URL
                if (urlIndex != urls.Count())
                {
                    Console.WriteLine($"{"Waiting".PadLeft(15)} {secondsDelayBetweenPageScrapes}s until next page scrape..");
                    Thread.Sleep(secondsDelayBetweenPageScrapes * 1000);
                }
            }

            // All page URLs have completed scraping.
            return;
        }

        // Takes a playwright element "div.product-tile", scrapes each of the desired data fields,
        //  and then returns a completed Product record
        private async static Task<Product> ScrapeProductElementToRecord(IElementHandle element, string url)
        {
            // Name
            var aTag = await element.QuerySelectorAsync("a.link");
            string? name = await aTag!.InnerTextAsync();

            // Image URL
            var imgDiv = await element.QuerySelectorAsync(".tile-image");
            string? imgUrl = await imgDiv!.GetAttributeAsync("src");

            // ID
            var linkHref = await aTag.GetAttributeAsync("href");   // get href to product page
            var fileName = linkHref!.Split('/').Last();            // get filename ending in .html
            string id = fileName.Split('.').First();               // extract ID from filename

            // Price
            var priceTag = await element.QuerySelectorAsync("span.now-price");
            var priceString = await priceTag!.InnerTextAsync();
            float currentPrice = float.Parse(priceString.Substring(1));

            // Source Website
            string sourceSite = "thewarehouse.co.nz";

            // Categories
            string[]? categories = DeriveCategoriesFromUrl(url);

            // Size
            string size = ExtractProductSize(name);

            // DatedPrice with date format 'Tue Jan 14 2023'
            string todaysDate = DateTime.Now.ToString("ddd MMM dd yyyy");
            DatedPrice todaysDatedPrice = new DatedPrice(todaysDate, currentPrice);

            // Create Price History array with a single element
            DatedPrice[] priceHistory = new DatedPrice[] { todaysDatedPrice };

            // Return completed Product record
            return (new Product(id, name!, size, currentPrice, categories!, sourceSite, priceHistory));
        }

        // Clean and Parse URLs to the most suitable format for scraping
        private static string[] CleanAndParseURLs(string[] urls)
        {
            for (int i = 0; i < urls.Count(); i++)
            {
                if (urls[i].Contains('?'))
                {
                    // Strip any existing query options off of URL
                    urls[i] = urls[i].Substring(0, urls[i].IndexOf('?'));
                }
                // Limit vendor to only the warehouse, not 3rd party sellers
                urls[i] += "?prefn1=marketplaceItem&prefv1=The Warehouse";
            }
            return urls;
        }

        // Shorthand function for logging with colour
        public static void Log(ConsoleColor color, string text)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = ConsoleColor.White;
        }

        // Derives food category names from url, if any categories are available
        // www.domain.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/milk
        // returns '[milk]'
        private static string[]? DeriveCategoriesFromUrl(string url)
        {
            // If url doesn't contain /food-drink/, return no category
            if (url.IndexOf("/food-drink/") < 0) return null;

            int categoriesStartIndex = url.IndexOf("/food-drink/");
            int categoriesEndIndex = url.Contains("?") ? url.IndexOf("?") : url.Length;
            string categoriesString = url.Substring(categoriesStartIndex, categoriesEndIndex - categoriesStartIndex);
            string lastCategory = categoriesString.Split("/").Skip(2).Last();

            return new string[] { lastCategory };
        }

        // Extract potential product size from product name
        // 'Anchor Blue Milk Powder 1kg' returns '1kg'
        private static string ExtractProductSize(string productName)
        {
            // \s = whitespace char, \d = digit, \w+ = 1 more word chars, $ = end
            string pattern = @"\s\d\w+$";

            string result = "";
            result = Regex.Match(productName, pattern).ToString().Trim();
            return result;
        }

        private static async Task RoutePlaywrightExclusions(bool logToConsole)
        {
            // Define excluded types and urls to reject
            // Define unnecessary types and ad/tracking urls to reject
            string[] typeExclusions = { "image", "stylesheet", "media", "font", "other" };
            string[] urlExclusions = { "googleoptimize.com", "gtm.js", "visitoridentification.js", "js-agent.newrelic.com",
            "cquotient.com", "googletagmanager.com", "cloudflareinsights.com", "dwanalytics", "edge.adobedc.net" };
            List<string> exclusions = urlExclusions.ToList<string>();

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
                    if (logToConsole) Log(ConsoleColor.Red, $"{req.Method} {req.ResourceType} - {trimmedUrl}");
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
        public enum UpsertResponse
        {
            NewProduct,
            Updated,
            AlreadyUpToDate,
            Failed
        }
    }
}