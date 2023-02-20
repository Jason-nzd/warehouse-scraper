﻿using Microsoft.Playwright;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

// Warehouse Scraper
// Scrapes product info and pricing from The Warehouse NZ's website.
// dryRunMode = true - will skip CosmosDB connections and only log to console

namespace WarehouseScraper
{
    public class Program
    {
        static bool dryRunMode = false;
        static int secondsDelayBetweenPageScrapes = 22;
        static string[] urls = new string[] {
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/milk",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/bread",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/cheese",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/eggs",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/chips-snacks/biscuits",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/chips-snacks/chips",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/chips-snacks/snack-muesli-bars"
        };

        public record DatedPrice(string date, float price);
        public record Product(string id, string name, float currentPrice, string[] category, string sourceSite, DatedPrice[] priceHistory, string imgUrl);

        enum UpsertResponse
        {
            NewProduct,
            Updated,
            AlreadyUpToDate,
            Failed
        }

        static CosmosClient? cosmosClient;
        static Database? database;
        static Container? cosmosContainer;

        public static async Task Main(string[] args)
        {
            // Handle arguments - 'dotnet run dry' will run in dry mode bypassing CosmosDB
            if (args.Length > 0)
            {
                if (args[0] == "dry")
                {
                    dryRunMode = true;
                    log(ConsoleColor.Yellow, $"\n(Dry Run mode on)");
                }
            }

            // If not in dry run mode, establish CosmosDB connection
            if (!dryRunMode)
            {
                try
                {
                    // Read from appsettings.json or appsettings.local.json
                    IConfiguration config = new ConfigurationBuilder()
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .AddEnvironmentVariables()
                        .Build();

                    cosmosClient = new CosmosClient(
                        accountEndpoint: config.GetRequiredSection("COSMOS_ENDPOINT").Get<string>(),
                        authKeyOrResourceToken: config.GetRequiredSection("COSMOS_KEY").Get<string>()!
                    );

                    database = cosmosClient.GetDatabase(id: "supermarket-prices");

                    // Container reference with creation if it does not already exist
                    cosmosContainer = await database.CreateContainerIfNotExistsAsync(
                        id: "supermarket-products",
                        partitionKeyPath: "/name",
                        throughput: 400
                    );

                    log(ConsoleColor.Yellow, $"\n(Connected to CosmosDB) {cosmosClient.Endpoint}");
                }
                catch (Microsoft.Azure.Cosmos.CosmosException e)
                {
                    log(ConsoleColor.Red, e.GetType().ToString());
                    log(ConsoleColor.Red, "Error Connecting to CosmosDB - check appsettings.json, endpoint or key may be expired");
                }
                catch (Exception e)
                {
                    log(ConsoleColor.Red, e.GetType().ToString());
                    log(ConsoleColor.Red, "Error Connecting to CosmosDB - make sure appsettings.json is created and contains:");
                    Console.Write(
                    "{\n" +
                    "\t\"COSMOS_ENDPOINT\": \"<your cosmosdb endpoint uri>\",\n" +
                    "\t\"COSMOS_KEY\": \"<your cosmosdb primary key>\"\n" +
                    "}\n\n"
                    );
                }
            }

            // Launch Playwright Headless Browser
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true }
            );
            var page = await browser.NewPageAsync();

            // Define unnecessary types and ad/tracking urls to reject
            string[] typeExclusions = { "image", "stylesheet", "media", "font", "other" };
            string[] urlExclusions = { "googleoptimize.com", "gtm.js", "visitoridentification.js", "js-agent.newrelic.com",
            "cquotient.com", "googletagmanager.com", "cloudflareinsights.com", "dwanalytics", "edge.adobedc.net" };
            List<string> exclusions = urlExclusions.ToList<string>();

            // Route with exclusions processed
            await page.RouteAsync("**/*", async route =>
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
                    //log(ConsoleColor.Red, $"{req.Method} {req.ResourceType} - {trimmedUrl}");
                    await route.AbortAsync();
                }
                else
                {
                    //log(ConsoleColor.White, $"{req.Method} {req.ResourceType} - {trimmedUrl}");
                    await route.ContinueAsync();
                }
            });

            // Open up each URL and run the scraping function
            await openEachURLForScraping(urls, page);

            // Complete after all URLs have been scraped
            log(ConsoleColor.Blue, "\nScraping Completed \n");

            // Clean up playwright browser and end program
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
                    log(ConsoleColor.Yellow, $"\nLoading Page [{urlIndex++}/{urls.Count()}] {url.PadRight(112).Substring(12, 100)}");
                    await page.GotoAsync(url);
                    await page.WaitForSelectorAsync("div.price-lockup-wrapper");
                }
                catch (System.Exception e)
                {
                    log(ConsoleColor.Red, "Unable to Load Web Page");
                    Console.Write(e.ToString());
                    return;
                }

                // Query all product card entries
                var productElements = await page.QuerySelectorAllAsync("div.product-tile");
                log(ConsoleColor.Yellow, productElements.Count.ToString().PadLeft(12) + " products found");

                // Create counters for logging purposes
                int newProductsCount = 0, updatedProductsCount = 0, upToDateProductsCount = 0;

                // Loop through every found playwright element
                foreach (var element in productElements)
                {
                    // Create Product object from playwright element
                    Product scrapedProduct = await scrapeProductElementToRecord(element, url);

                    if (!dryRunMode)
                    {
                        // Try upsert to CosmosDB
                        UpsertResponse response = await upsertProductToCosmosDB(scrapedProduct);

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
                            "$" + scrapedProduct.currentPrice + "\t| " + scrapedProduct.category.Last()
                        );
                    }
                }

                if (!dryRunMode)
                {
                    // Log consolidated CosmosDB stats for entire page scrape
                    log(ConsoleColor.Blue, $"{"CosmosDB:".PadLeft(15)} {newProductsCount} new products, " +
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
        async static Task<Product> scrapeProductElementToRecord(IElementHandle element, string url)
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
            string[]? categories = deriveCategoriesFromUrl(url);

            // DatedPrice with date format 'Tue Jan 14 2023'
            string todaysDate = DateTime.Now.ToString("ddd MMM dd yyyy");
            DatedPrice todaysDatedPrice = new DatedPrice(todaysDate, currentPrice);

            // Create Price History array with a single element
            DatedPrice[] priceHistory = new DatedPrice[] { todaysDatedPrice };

            // Return completed Product record
            return (new Product(id, name!, currentPrice, categories!, sourceSite, priceHistory, imgUrl!));
        }

        // Takes a scraped Product, and tries to insert or update an existing Product on CosmosDB
        async static Task<UpsertResponse> upsertProductToCosmosDB(Product scrapedProduct)
        {
            bool productAlreadyOnCosmosDB = false;
            Product? dbProduct = null;

            try
            {
                // Check if product already exists on CosmosDB, throws exception if not found
                var response = await cosmosContainer!.ReadItemAsync<Product>(
                    scrapedProduct.id,
                    new PartitionKey(scrapedProduct.name)
                );

                // Set local product from CosmosDB resource
                dbProduct = response.Resource;
                if (response.StatusCode == System.Net.HttpStatusCode.OK) productAlreadyOnCosmosDB = true;
            }
            catch (Microsoft.Azure.Cosmos.CosmosException e)
            {
                if (e.StatusCode == System.Net.HttpStatusCode.NotFound)
                    productAlreadyOnCosmosDB = false;
            }

            if (productAlreadyOnCosmosDB)
            {
                // Check if price has changed
                float dbPrice = dbProduct!.currentPrice;
                float scrapedPrice = scrapedProduct.currentPrice;

                if (dbPrice != scrapedPrice)
                {
                    // Price has changed, so we can create an updated Product with the changes
                    DatedPrice[] updatedHistory = dbProduct.priceHistory;
                    updatedHistory.Append(scrapedProduct.priceHistory[0]);

                    Product updatedProduct = new Product(
                        dbProduct.id,
                        dbProduct.name,
                        scrapedProduct.currentPrice,
                        scrapedProduct.category,
                        dbProduct.sourceSite,
                        updatedHistory,
                        dbProduct.imgUrl
                    );

                    // Log price change with different verb and colour depending on price change direction
                    bool priceTrendingDown = (scrapedPrice < dbPrice);
                    string priceTrendText = "Price " + (priceTrendingDown ? "Decreased" : "Increased").PadLeft(15);

                    log(priceTrendingDown ? ConsoleColor.Green : ConsoleColor.Red,
                        $"{priceTrendText} {dbProduct.name.PadRight(50).Substring(0, 50)} from " +
                        $"${dbProduct.currentPrice} to ${scrapedProduct.currentPrice}"
                    );

                    try
                    {
                        // Upsert the updated product back to CosmosDB
                        await cosmosContainer!.UpsertItemAsync<Product>(updatedProduct, new PartitionKey(updatedProduct.name));
                        return UpsertResponse.Updated;
                    }
                    catch (Microsoft.Azure.Cosmos.CosmosException e)
                    {
                        Console.WriteLine($"CosmosDB Upsert Error on existing Product: {e.StatusCode}");
                        return UpsertResponse.Failed;
                    }
                }
                else
                {
                    return UpsertResponse.AlreadyUpToDate;
                }
            }
            else
            {
                try
                {
                    // No existing product was found, upload to CosmosDB
                    await cosmosContainer!.UpsertItemAsync<Product>(scrapedProduct, new PartitionKey(scrapedProduct.name));

                    Console.WriteLine(
                        $"{"New Product:".PadLeft(15)} {scrapedProduct.id.PadRight(10)} | " +
                        $"{scrapedProduct.name!.PadRight(50).Substring(0, 50)}" +
                        $" | ${scrapedProduct.currentPrice}\t| ${scrapedProduct.category.Last()}"
                    );

                    return UpsertResponse.NewProduct;
                }
                catch (Microsoft.Azure.Cosmos.CosmosException e)
                {
                    Console.WriteLine($"{"CosmosDB:".PadLeft(15)} Upsert Error for new Product: {e.StatusCode}");
                    return UpsertResponse.Failed;
                }
            }
        }

        // Clean and Parse URLs to the most suitable format for scraping
        static string[] cleanAndParseURLs(string[] urls)
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
        static void log(ConsoleColor color, string text)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = ConsoleColor.White;
        }

        // Derives food category names from url, if any categories are available
        // www.domain.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/milk
        // returns '[pantry, milk-bread, milk]'
        static string[]? deriveCategoriesFromUrl(string url)
        {
            // If url doesn't contain /browse/, return no category
            if (url.IndexOf("/food-drink/") < 0) return null;

            int categoriesStartIndex = url.IndexOf("/food-drink/");
            int categoriesEndIndex = url.Contains("?") ? url.IndexOf("?") : url.Length;
            string categoriesString = url.Substring(categoriesStartIndex, categoriesEndIndex - categoriesStartIndex);
            string[] splitCategories = categoriesString.Split("/").Skip(2).ToArray();

            return splitCategories;
        }
    }
}