using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Playwright;
using static Scraper.Program;

namespace ScraperTests
{
    [TestClass]
    public class ScraperTests
    {
        [TestMethod]
        public async void Playwright_Connected()
        {
            // Launch Playwright Browser in headless mode
            var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true }
            );
            Assert.IsTrue(browser.IsConnected);
        }
    }
}