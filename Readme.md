# The Warehouse Scraper
[![.NET](https://img.shields.io/badge/.NET-8.0+-5C2D91?logo=dotnet)](https://dotnet.microsoft.com/)
[![Playwright](https://img.shields.io/badge/Playwright-latest-2EAD33?logo=playwright)](https://playwright.dev/)

A powerful web scraper that extracts product pricing and information from the **The Warehouse NZ** website. Logs data to console and optionally stores price history in Azure CosmosDB.

## ✨ Features
- 🎭 **Headless Browser** — Powered by **Playwright**
- 📊 **Price Tracking** — Store historical price data to track changes over time
- 🖼️ **Image Processing** — Send product images to a REST API for resizing and analysis

## 🚀 Quick Start

### Prerequisites
- [.NET SDK 8.0 or newer](https://dotnet.microsoft.com/download)

### Installation
```bash
# Clone the repository
git clone https://github.com/Jason-nzd/warehouse-scraper
cd src

# Restore and build dependencies
dotnet restore
dotnet build

# Run the scraper (dry run mode)
dotnet run
```

> 💡The first run will automatically install Playwright browser runtimes.

### 📋 Sample Console Output
```cmd
  ID     | Name                               | Size    | Price | Unit Price
-----------------------------------------------------------------------------
 R254367 | Cola Can Mixed Tray 330ml 24 Pack  | 7.92L   | $ 25  | $3.16 /L
 R765575 | Cola Tray 330ml 24 Pack            | 7.92L   | $ 25  | $3.16 /L
 R884667 | 99% Sugar Free Soft Drink 24x      | 8.4L    | $ 11  | $1.31 /L
 R987739 | Pop Max Can 355ml 24 Pack          | 8.52L   | $ 20  | $2.35 /L
 R168505 | Mountain Minis 250ml 10 Pack       | 2.5L    | $  8  | $3.2 /L
 R909803 | Pop Minis 250ml 10 Pack            | 2.5L    | $  8  | $3.2 /L
 R678645 | Cola Zero Sugar Can 24x330ml       | 7.92L   | $ 25  | $3.16 /L
```

---

## ⚙️ appsettings.json

> Optional advanced parameters can be configured in `appsettings.json`:

Store product information and price snapshots in CosmosDB by defining:

```json
{
  "COSMOS_ENDPOINT": "<your-cosmosdb-endpoint-uri>",
  "COSMOS_KEY": "<your-cosmosdb-primary-key>",
  "COSMOS_DB": "<database-name>",
  "COSMOS_CONTAINER": "<container-name>"
}
```

Send scraped product images to a REST API for processing:
```json
{
  "IMAGE_PROCESS_API_URL": "<your-rest-api-endpoint>"
}
```

### 💻 Command-Line Usage
- ```dotnet run``` - Dry run - logs products to console only
- ```dotnet run db``` - Stores scraped data into CosmosDB
- ```dotnet run db images``` - Store to DB + send images to API
- ```dotnet run headed``` - Run with visible browser for debugging

### 📄 Sample Cosmos DB Document
Products stored in Cosmos DB include price history for trend analysis:

```json
{
    "id": "R1234567",
    "name": "Caramel Lite Milk",
    "size": "1L",
    "category": "milk",
    "priceHistory": [
        {
            "date": "2023-07-02",
            "price": 2.95
        },
        {
            "date": "2024-12-30",
            "price": 3.25
        },
        {
            "date": "2025-06-30",
            "price": 3.4
        },
        {
            "date": "2025-12-29",
            "price": 3.85
        }
    ],
    "lastChecked": "2026-01-04",
    "unitPrice": "3.31/L"
}
```