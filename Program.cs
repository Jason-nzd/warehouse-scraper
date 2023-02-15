using Microsoft.Playwright;

// Warehouse Scraper
// Scrapes product info and pricing from The Warehouse NZ's website.

public class WarehouseScraper
{
    public record DatedPrice(string date, float price);
    public record Product(string name, string id, string imgUrl, float currentPrice, string sourceSite);

    public static async Task Main()
    {
        string[] urls = new string[] {
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/milk",
        "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/bread"
        };

        // Clean URLs
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

        // Launch browser without headless option
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = false }
        );
        var page = await browser.NewPageAsync();

        // Open up each URL and run the scraping function
        await openAllUrlsForScraping(urls, page);

        // Complete after all URLs have been scraped
        Console.WriteLine("\nScraping Completed \n");
        await browser.CloseAsync();
        return;
    }

    async static Task openAllUrlsForScraping(string[] urls, IPage page)
    {
        int urlIndex = 1;

        foreach (var url in urls)
        {
            // Try load page and wait for full page to dynamically load in
            try
            {
                Console.WriteLine($"\nLoading Page [{urlIndex++}/{urls.Count()}] {url}");
                await page.GotoAsync(url);
                await page.WaitForSelectorAsync("div.price-lockup-wrapper");
            }
            catch (System.Exception)
            {
                Console.WriteLine("Unable to load web page");
                return;
            }

            // Query all product card entries
            var productElements = await page.QuerySelectorAllAsync("div.product-tile");
            Console.WriteLine(productElements.Count.ToString() + " products found");

            // Prepare a table to log to console
            Console.WriteLine(
                " ID ".PadLeft(10) +
                "Name".PadRight(50) + "\t" +
                "Price");
            Console.WriteLine("".PadRight(70, '-'));

            foreach (var element in productElements)
            {
                Product scrapedProduct = await scrapeProductElementToRecord(element);
                // Send to CosmosDB

                // Print a log for each product
                Console.WriteLine(
                    scrapedProduct.id.PadLeft(10) + "   " +
                    scrapedProduct.name!.PadRight(50).Substring(0, 50) + "\t" +
                    "$" + scrapedProduct.currentPrice + "\t"
                );
            }

            // This URL has now completed scraping. A delay is added in-between each subsequent URL
            //Thread.Sleep(3000);
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

        string sourceSite = "thewarehouse.co.nz";

        // Return completed Product record
        return (new Product(name!, id, imgUrl!, currentPrice, sourceSite));
    }
}
