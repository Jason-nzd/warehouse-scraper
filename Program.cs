using Microsoft.Playwright;
using Microsoft.Azure.Cosmos;

// Warehouse Scraper
// Scrapes product info and pricing from The Warehouse NZ's website.
// dryRunMode = true - will skip CosmosDB connections and only log to console

public class WarehouseScraper
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
    public record Product(string id, string name, float currentPrice, string sourceSite, DatedPrice[] priceHistory, string imgUrl);

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
        // Handle arguments - 'dotnet run dry' will run in dry mode
        if (args.Length > 0)
        {
            if (args[0] == "dry") dryRunMode = true;
        }

        // Launch Playwright Headless Browser
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true }
        );
        var page = await browser.NewPageAsync();

        // If dry run mode on, skip CosmosDB
        if (dryRunMode) log(ConsoleColor.Yellow, $"\n(Dry Run mode on)");

        // If not in dry run mode, establish CosmosDB connections
        else if (!dryRunMode)
        {
            try
            {
                cosmosClient = new CosmosClient(
                    accountEndpoint: Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")!,
                    authKeyOrResourceToken: Environment.GetEnvironmentVariable("COSMOS_KEY")!
                );

                database = cosmosClient.GetDatabase(id: "supermarket-prices");

                // Container reference with creation if it does not already exist
                cosmosContainer = await database.CreateContainerIfNotExistsAsync(
                    id: "warehouse-products",
                    partitionKeyPath: "/name",
                    throughput: 400
                );

                log(ConsoleColor.Yellow, $"\n(Connected to CosmosDB) {cosmosClient.Endpoint}");
            }
            catch (System.ArgumentNullException)
            {
                log(ConsoleColor.Red, "Error Connecting to CosmosDB - make sure env variables are set:\n");
                Console.WriteLine("$env:COSMOS_ENDPOINT = \"<cosmos-account-URI>\"");
                Console.WriteLine("$env:COSMOS_KEY = \"<cosmos-account-PRIMARY-KEY>\"\n");
                await browser.CloseAsync();
                return;
            }
        }

        // Open up each URL and run the scraping function
        await openEachURLForScraping(urls, page, cosmosContainer!);

        // Complete after all URLs have been scraped
        log(ConsoleColor.Blue, "\nScraping Completed \n");
        await browser.CloseAsync();
        return;
    }

    async static Task openEachURLForScraping(string[] urls, IPage page, Container cosmosContainer)
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
            Console.WriteLine(productElements.Count.ToString() + " products found");

            // Create counters for logging purposes
            int newProductsCount = 0, updatedProductsCount = 0, upToDateProductsCount = 0;

            // Loop through every found playwright element
            foreach (var element in productElements)
            {
                // Create Product object from playwright element
                Product scrapedProduct = await scrapeProductElementToRecord(element);

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
                        "$" + scrapedProduct.currentPrice
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
                log(ConsoleColor.Blue, $"Waiting {secondsDelayBetweenPageScrapes}s until next page scrape..");
                Thread.Sleep(secondsDelayBetweenPageScrapes * 1000);
            }
        }

        // All page URLs have completed scraping.
        return;
    }

    // Takes a playwright element "div.product-tile", scrapes each of the desired data fields,
    //  and then returns a completed Product record
    async static Task<Product> scrapeProductElementToRecord(IElementHandle element)
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

        // DatedPrice with date format 'Tue Jan 14 2023'
        string todaysDate = DateTime.Now.ToString("ddd MMM dd yyyy");
        DatedPrice todaysDatedPrice = new DatedPrice(todaysDate, currentPrice);

        // Create Price History array with a single element
        DatedPrice[] priceHistory = new DatedPrice[] { todaysDatedPrice };

        // Return completed Product record
        return (new Product(id, name!, currentPrice, sourceSite, priceHistory, imgUrl!));
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
            float newPrice = scrapedProduct.currentPrice;
            if (dbPrice != newPrice)
            {
                // Price has changed, so we can create an updated Product with the changes
                DatedPrice[] updatedHistory = dbProduct.priceHistory;
                updatedHistory.Append(scrapedProduct.priceHistory[0]);

                Product updatedProduct = new Product(
                    dbProduct.id,
                    dbProduct.name,
                    scrapedProduct.currentPrice,
                    dbProduct.sourceSite,
                    updatedHistory,
                    dbProduct.imgUrl
                );

                // Log price change with different verb and colour depending on price change direction
                bool priceTrendingDown = (newPrice < dbPrice);
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
                    $"{"New Product:".PadLeft(15)} {scrapedProduct.name!.PadRight(50).Substring(0, 50)}" +
                    $"\t ${scrapedProduct.currentPrice}"
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
}
