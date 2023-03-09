using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Scraper.Utilities;

namespace ScraperTests
{
    [TestClass]
    public class UtilitiesTests
    {
        [TestMethod]
        public void DeriveCategoryFromUrl_ArrayLengthForUncategorised()
        {
            string url = "asdf";
            var result = DeriveCategoryFromUrl(url, "/food-drink/");
            Assert.AreEqual<int>(result.Length, 1);
        }

        [TestMethod]
        public void DeriveCategoryFromUrl_UncategorisedValue()
        {
            string url = "asdf";
            var result = DeriveCategoryFromUrl(url, "/food-drink/");
            Assert.AreEqual<string>(result, "Uncategorised");
        }

        [TestMethod]
        public void DeriveCategoryFromUrl_ExcludesQueryParameters()
        {
            string url = "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/canned-food?asdfr=gfd";
            var result = DeriveCategoryFromUrl(url, "/food-drink/");
            Assert.AreEqual<string>(result, "canned-food");
        }

        [TestMethod]
        public void DeriveCategoryFromUrl_GetsCorrectCategories()
        {
            string url =
                "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/ingredients-sauces-oils/table-sauces";
            var result = DeriveCategoryFromUrl(url, "/food-drink/");
            Assert.AreEqual<string>(result, "table-sauces");
        }

        [TestMethod]
        public void DeriveCategoryFromUrl_WorksWithoutHttpSlash()
        {
            string url = "www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/canned-food";
            var result = DeriveCategoryFromUrl(url, "/food-drink/");
            Assert.AreEqual<string>(result, "canned-food");
        }

        [TestMethod]
        public void ExtractProductSize_1kg()
        {
            string productName = "Anchor Blue Milk Powder 1kg";
            Assert.AreEqual<string>(ExtractProductSize(productName), "1kg");
        }

        [TestMethod]
        public void ExtractProductSize_255g()
        {
            string productName = "Lee Kum Kee Panda Oyster Sauce 255g";
            Assert.AreEqual<string>(ExtractProductSize(productName), "255g");
        }

        [TestMethod]
        public void ExtractProductSize_NoSize()
        {
            string productName = "Anchor Blue Milk Powder";
            Assert.AreEqual<string>(ExtractProductSize(productName), "");
        }

        [TestMethod]
        public void ExtractProductSize_400ml()
        {
            string productName = "Trident Premium Coconut Cream 400ml";
            Assert.AreEqual<string>(ExtractProductSize(productName), "400ml");
        }
    }
}