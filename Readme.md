# The Warehouse Scraper

Scrapes product pricing and info from The Warehouse NZ website. Price snapshots can be saved to a database, or this program can simply log to console.

## Setup

First clone this repo, then restore .NET packages and build the project with:

```powershell
dotnet restore && dotnet build
```

Playwright web browsers must be downloaded and installed using:

```powershell
pwsh bin/Debug/net6.0/playwright.ps1 install
```

If running in dry mode, the program is now ready to use.

If using CosmosDB, then 2 environment variables need to be set:

```powershell
$env:COSMOS_ENDPOINT = "<cosmos-account-URI>"
$env:COSMOS_KEY = "<cosmos-account-PRIMARY-KEY>"
```

## Usage

To dry run the scraper, logging each product to the console:

```powershell
dotnet run dry
```

To run the scraper and save each product to the database:

```powershell
dotnet run
```

## Sample Product Stored in CosmosDB

```json
{
    "id": "W12345678",
    "name": "Puhoi Valley Caramel Milk 300ml",
    "currentPrice": 3.6,
    "priceHistory": [
       {
            "date": "Fri Feb 10 2023",
            "price": 2.9
       },
       {
            "date": "Thu Feb 16 2023",
            "price": 3.6
       },
    ]
}
```

## Sample Dry Run Output

```powershell
       ID    Name                                               Price
----------------------------------------------------------------------
   R123123   Meadow Fresh Original Homogenised UHT 1L           $3
  R2342342   Anchor Blue Milk Powder 1kg                        $14.3
   R345345   Meadow Fresh Lite UHT 1L                           $3
  R4564566   Cow & Gate Blue Standard Milk 2L                   $3
  R5675675   Cow & Gate Red Fat Milk Plastic 2L                 $3
  R6786788   Tararua Dairy Co Protein Hit Chocolate Fresh Flavo $4.6
```
