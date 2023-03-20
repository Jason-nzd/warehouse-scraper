using System.Text.RegularExpressions;
using static Scraper.Program;

namespace Scraper
{
    public struct CategorisedURL
    {
        public string url;
        public string[] categories;

        public CategorisedURL(string url, string[] categories)
        {
            this.url = url;
            this.categories = categories;
        }
    }

    public partial class Utilities
    {
        // ParseLineToCategorisedURL()
        // ---------------------------
        // Parses a textLine containing a url and optional overridden category names to a CategorisedURL
        public static CategorisedURL? ParseLineToCategorisedURL(
            string textLine,
            string urlShouldContain = ".co.nz",
            string replaceQueryParamsWith = "")
        {
            // Get url from the first section
            string url = textLine.Split(' ').First();

            // If url doesn't contain desired section, return null
            if (!url.Contains(urlShouldContain)) return null;

            // Optimise query parameters
            url = OptimiseURLQueryParameters(url, replaceQueryParamsWith);

            // Derive category from url
            string[] categories = { DeriveCategoryFromURL(url) };

            // If overridden categories are provided, override the derived categories
            string overriddenCategoriesSection = textLine.Split(' ').Last();
            if (overriddenCategoriesSection.Contains("categories="))
            {
                categories = overriddenCategoriesSection.Replace("categories=", "").Split(",");
            }

            return new CategorisedURL(url, categories);
        }

        // Parses urls and optimises query options for best results, returns null if invalid
        public static string OptimiseURLQueryParameters(string url, string replaceQueryParamsWith)
        {
            string cleanURL = url;

            // If url contains 'search?', keep the search parameter but strip the rest
            if (url.Contains("search?"))
            {
                // Strip out anything after the first & symbol, or until the end of the string
                int lastIndex = url.Contains('&') ? url.IndexOf('&') : url.Length - 1;
                cleanURL = url.Substring(0, lastIndex) + "&";
            }

            // Else strip all query parameters
            else if (url.Contains('?'))
            {
                cleanURL = url.Substring(0, url.IndexOf('?')) + "?";
            }

            // If there were no existing query parameters, ensure a ? is added
            else cleanURL += "?";

            // Replace query parameters with optimised ones,
            //  such as limiting to certain sellers,
            //  or showing a higher number of products
            cleanURL += replaceQueryParamsWith;

            // Return cleaned url
            return cleanURL;
        }

        // UploadImageUsingRestAPI() - sends an image url to an Azure Function API to be uploaded
        public async static Task UploadImageUsingRestAPI(string imgUrl, Product product)
        {
            // Get AZURE_FUNC_URL from appsettings.json
            // Example format:
            // https://<func-app-name>.azurewebsites.net/api/ImageToS3?code=<func-auth-code>&destination=s3://<bucket>/<optional-path>/
            string? funcUrl = config!.GetSection("AZURE_FUNC_URL").Value;

            // Check funcUrl is valid
            if (!funcUrl!.Contains("http"))
                throw new Exception("AZURE_FUNC_URL in appsettings.json invalid. Should be in format:\n\n" +
                "\"AZURE_FUNC_URL\": \"https://<func-app-name>.azurewebsites.net/api/ImageToS3?code=<func-auth-code>&destination=s3://<bucket>/<optional-path>/\"");

            // Perform http get
            string restUrl = funcUrl + product.id + "&source=" + imgUrl;
            var response = await httpclient.GetAsync(restUrl);
            var responseMsg = await response.Content.ReadAsStringAsync();

            // Log for successful upload of new image
            if (responseMsg.Contains("S3 Upload of Full-Size and Thumbnail WebPs"))
            {
                Log(
                    ConsoleColor.Gray,
                    $"  New Image  : {product.id.PadLeft(8)} | {product.name.PadRight(50).Substring(0, 50)}"
                );
            }
            else if (responseMsg.Contains("already exists"))
            {
                // Do not log for existing images
            }
            else
            {
                // Log any other errors that may have occurred
                Console.Write(restUrl + "\n" + responseMsg);
            }
            return;
        }

        // Validates product values are within normal ranges
        public static bool IsValidProduct(Product product)
        {
            try
            {
                if (product.name.Length < 4 || product.name.Length > 100) return false;
                if (product.id.Length < 2 || product.id.Length > 20) return false;
                if (
                  product.currentPrice <= 0 || product.currentPrice > 999
                )
                {
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Reads lines from a txt file, then return as a List
        public static List<string>? ReadLinesFromFile(string fileName)
        {
            try
            {
                List<string> result = new List<string>();
                string[] lines = File.ReadAllLines(@fileName);

                if (lines.Length == 0) throw new Exception("No lines found in " + fileName);

                foreach (string line in lines)
                {
                    if (line != null) result.Add(line);
                }

                return result;
            }
            catch (System.Exception)
            {
                LogError("Unable to read file " + fileName + "\n");
                return null;
            }
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

        // Derives category name from url by taking the last /bracket/
        // www.domain.co.nz/c/food-pets-household/food-drink/pantry/milk-bread/milk
        // returns milk
        public static string DeriveCategoryFromURL(string url)
        {
            int categoriesEndIndex = url.Contains("?") ? url.IndexOf("?") : url.Length;
            string categoriesString =
                url.Substring(
                    0,
                    categoriesEndIndex
                );
            string lastCategory = categoriesString.Split("/").Last();
            return lastCategory;
        }

        // Shorthand function for logging with provided colour
        public static void Log(ConsoleColor color, string text)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = ConsoleColor.White;
        }

        // Shorthand function for logging with red colour
        public static void LogError(string text)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(text);
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}