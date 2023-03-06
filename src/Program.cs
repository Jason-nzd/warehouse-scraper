using Microsoft.Playwright;
using Microsoft.Azure.Cosmos;
using System.Text.RegularExpressions;
using static WarehouseScraper.CosmosDB;

// Warehouse Scraper
// Scrapes product info and pricing from The Warehouse NZ's website.
// dryRunMode = true - will skip CosmosDB connections and only log to console

namespace WarehouseScraper
{
    public class Program
    {
        static int secondsDelayBetweenPageScrapes = 22;

        public record Product(
            string id,
            string name,
            string size,
            float currentPrice,
            string[] category,
            string sourceSite,
            DatedPrice[] priceHistory,
            string lastUpdated
        );
        public record DatedPrice(string date, float price);

        // Singletons for CosmosDB and Playwright
        public static CosmosClient? cosmosClient;
        public static Database? database;
        public static Container? cosmosContainer;
        public static IPlaywright? playwright;
        public static IBrowser? browser;
        public static IPage? playwrightPage;

        public static async Task Main(string[] args)
        {
            // Handle arguments - 'dotnet run dry' will run in dry mode, bypassing CosmosDB
            if (args.Length > 0)
            {
                if (args[0] == "dry") dryRunMode = true;
                Log(ConsoleColor.Yellow, $"\n(Dry Run mode on)");
            }

            // Establish Playwright browser
            await EstablishPlaywright();

            // Connect to CosmosDB - end program if unable to connect
            if (!dryRunMode)
            {
                if (!await CosmosDB.EstablishConnection(
                       databaseName: "supermarket-prices",
                       partitionKey: "/name",
                       containerName: "products"
                   )) return;
            }

            // Read URLs from file
            List<string> urls = ReadURLsFromFile("URLs.txt");

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

                    // Query all product card entries
                    var productElements = await playwrightPage.QuerySelectorAllAsync("div.product-tile");
                    Log(ConsoleColor.Yellow, productElements.Count + " products found");

                    // Create counters for logging purposes
                    int newCount = 0, priceUpdatedCount = 0, nonPriceUpdatedCount = 0, upToDateCount = 0;

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
                        }
                        else
                        {
                            // In Dry Run mode, print a log row for every product
                            Console.WriteLine(
                                scrapedProduct.id.PadLeft(9) + " | " +
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
                        Log(ConsoleColor.Blue, $"{"CosmosDB:".PadLeft(15)} {newCount} new products, " +
                        $"{priceUpdatedCount} prices updated, {nonPriceUpdatedCount} info updated, " +
                        $"{upToDateCount} already up-to-date");
                    }
                }
                catch (TimeoutException)
                {
                    Log(ConsoleColor.Red, "Unable to Load Web Page - timed out after 30 seconds");
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
                Log(ConsoleColor.Red, "Browser must be manually installed using: \npwsh bin/Debug/net6.0/playwright.ps1 install\n");
                throw;
            }
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
        public static string[] DeriveCategoriesFromUrl(string url)
        {
            // If url doesn't contain /food-drink/, return no category
            if (url.IndexOf("/food-drink/") > 0)
            {
                int categoriesStartIndex = url.IndexOf("/food-drink/");
                int categoriesEndIndex = url.Contains("?") ? url.IndexOf("?") : url.Length;
                string categoriesString = url.Substring(categoriesStartIndex, categoriesEndIndex - categoriesStartIndex);
                string lastCategory = categoriesString.Split("/").Skip(2).Last();

                return new string[] { lastCategory };
            }
            else return new string[] { "Uncategorised" };
        }

        // Extract potential product size from product name
        // 'Anchor Blue Milk Powder 1kg' returns '1kg'
        public static string ExtractProductSize(string productName)
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

        // Reads urls from a txt file, parses urls, and optimises query options for best results
        private static List<string> ReadURLsFromFile(string fileName)
        {
            List<string> urls = new List<string>();

            try
            {
                string[] lines = File.ReadAllLines(@fileName);

                if (lines.Length == 0) throw new Exception("No lines found in URLs.txt");

                foreach (string line in lines)
                {
                    // If line contains .co.nz it should be a URL
                    if (line.Contains(".co.nz"))
                    {
                        string cleanURL = line;
                        // If url contains ? it has query options already set
                        if (line.Contains('?'))
                        {
                            // Strip any existing query options off of URL
                            cleanURL = line.Substring(0, line.IndexOf('?'));
                        }
                        // Limit vendor to only the warehouse, not 3rd party sellers
                        cleanURL += "?prefn1=marketplaceItem&prefv1=The Warehouse&srule=best-sellers";

                        // Add completed url into list
                        urls.Add(cleanURL);
                    }
                }
                return urls;
            }
            catch (Exception e)
            {
                Console.Write("Unable to read file " + fileName + "\n" + e.ToString());
                throw;
            }
        }

        private static bool dryRunMode = false;

    }
}