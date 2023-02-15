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

## Usage

To dry run the scraper, logging each product to the console:

```powershell
dotnet run dry
```

To run the scraper and save each product to the database.

```powershell
dotnet run
```

## Sample Output

```powershell
       ID    Name                                               Price
----------------------------------------------------------------------
   R935075   Meadow Fresh Original Homogenised UHT 1L           $3
  R2700254   Anchor Blue Milk Powder 1kg                        $14.3
   R935076   Meadow Fresh Lite UHT 1L                           $3
  R1528048   Cow & Gate Blue Standard Milk 2L                   $3
  R2480105   Cow & Gate Red Fat Milk Plastic 2L                 $3
  R1933103   Tararua Dairy Co Protein Hit Chocolate Fresh Flavo $4.6
```
