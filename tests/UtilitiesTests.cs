using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Scraper.Utilities;

namespace ScraperTests
{
    [TestClass]
    public class UtilitiesTests
    {
        [TestMethod]
        public void DeriveCategoryFromURL_ExcludesQueryParameters()
        {
            string url = "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/canned-food?asdfr=gfd";
            var result = DeriveCategoryFromURL(url);
            Assert.AreEqual<string>(result, "canned-food");
        }

        [TestMethod]
        public void DeriveCategoryFromURL_GetsCorrectCategories()
        {
            string url =
                "https://www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/ingredients-sauces-oils/table-sauces";
            var result = DeriveCategoryFromURL(url);
            Assert.AreEqual<string>(result, "table-sauces");
        }

        [TestMethod]
        public void DeriveCategoryFromURL_WorksWithoutHttpSlash()
        {
            string url = "www.thewarehouse.co.nz/c/food-pets-household/food-drink/pantry/canned-food";
            var result = DeriveCategoryFromURL(url);
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

        [TestMethod]
        public void DeriveUnitPriceString_2L()
        {
            string? unitPriceString = DeriveUnitPriceString("Bottle 2L", 6.5f);
            Assert.AreEqual<string>(unitPriceString, "3.25/L/2", unitPriceString);
        }

        [TestMethod]
        public void DeriveUnitPriceString_Multiplier()
        {
            string? unitPriceString = DeriveUnitPriceString("Pouch 4 x 107mL", 6.5f);
            Assert.AreEqual<string>(unitPriceString, "15.19/L/428", unitPriceString);
        }

        [TestMethod]
        public void DeriveUnitPriceString_Decimal()
        {
            string? unitPriceString = DeriveUnitPriceString("Bottle 1.5L", 3f);
            Assert.AreEqual<string>(unitPriceString, "2/L/1.5", unitPriceString);
        }

        [TestMethod]
        public void DeriveUnitPriceString_SimpleKg()
        {
            string? unitPriceString = DeriveUnitPriceString("kg", 3f);
            Assert.AreEqual<string>(unitPriceString, "3/kg/1", unitPriceString);
        }


    }
}