# The Warehouse Scraper

Scrapes product pricing and info from the Warehouse NZ website.
Product information and price snapshots can be stored on Azure CosmosDB, or this program can simply log to console. Images can be sent to an API for resizing, analysis and other processing.

The scraper is powered by `Microsoft Playwright`. It requires `.NET SDK` & `Powershell` to run. Azure CosmosDB and image processing are optional.

## Quick Setup

With `.NET SDK` installed, clone this repo, change directory into `/src`, then restore and build .NET packages with:

```powershell
dotnet restore
dotnet build
```

Playwright Chromium web browser must be downloaded and installed using:

```cmd
pwsh bin/Debug/net6.0/playwright.ps1 install chromium
```

The program is now ready to use and will scrape all URLs placed in `Urls.txt`.

```cmd
dotnet run
```

## Sample Dry Run Output

```cmd
  ID      | Name                                    | Size     | Price | Unit Price
-------------------------------------------------------------------------------------
 R254367 | Cola Can Mixed Tray 330ml 24 Pack        | 7.92L    | $ 25  | $3.16 /L
 R765575 | Cola Tray 330ml 24 Pack                  | 7.92L    | $ 25  | $3.16 /L
 R884667 | Mixed Tray 99% Sugar Free Soft Drink 24x | 8.4L     | $ 11  | $1.31 /L
 R987739 | Pop Max Can 355ml 24 Pack                | 8.52L    | $ 20  | $2.35 /L
 R168505 | Mountain Minis 250ml 10 Pack             | 2.5L     | $  8  | $3.2 /L
 R909803 | Pop Minis 250ml 10 Pack                  | 2.5L     | $  8  | $3.2 /L
 R678645 | Cola Zero Sugar Can 24x330ml             | 7.92L    | $ 25  | $3.16 /L
```


## Optional Setup with appsettings.json

To set optional advanced parameters, edit `appsettings.json`.

If writing to CosmosDB, set the CosmosDB variables with the format:

```json
{
  "COSMOS_ENDPOINT": "<your cosmosdb endpoint uri>",
  "COSMOS_KEY": "<your cosmosdb primary key>",
  "COSMOS_DB": "<cosmosdb database>",
  "COSMOS_CONTAINER": "<cosmosdb container>",
}
```

Images can be sent off to a REST API for processing with:

```json
{
  "IMAGE_PROCESS_API_URL": "<rest api url>",
}
```

## Command-Line Usage

To dry run the scraper, logging each product to the console:

```powershell
dotnet run
```

To run the scraper with both logging and storing of each product to the database:

```powershell
dotnet run db
```

Images can be sent off for processing to an API (defined in appsettings).
```powershell
dotnet run db images
```

The browser defaults to headless mode but can changed to headed for debugging and reliability.
```powershell
dotnet run headed
```


## Sample Product Stored in CosmosDB

This sample was re-run on multiple days to capture changing prices.

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
