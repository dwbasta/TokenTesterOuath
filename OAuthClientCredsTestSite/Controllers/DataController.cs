using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OAuthClientCredsTestSite.Controllers;

[ApiController]
[Route("api/data")]
public sealed class DataController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "ApiRead")]
    public IActionResult Get()
    {
        return Ok(new
        {
            message = "Read allowed.",
            source = "Krusty Krab employee directory (fictional demo data)",
            retrievedAtUtc = DateTime.UtcNow,
            employees = new[]
            {
                new
                {
                    employeeId = "KK-001",
                    name = "SpongeBob SquarePants",
                    jobTitle = "Fry Cook",
                    department = "Kitchen Operations",
                    yearsOfEmployment = 10,
                    employmentStatus = "FullTime",
                    achievements = new[]
                    {
                        "Employee of the Month",
                        "Perfect attendance",
                        "Completed advanced spatula safety training"
                    },
                    salary = new { amount = 15.00m, currency = "USD", payFrequency = "Hourly" },
                    address = new
                    {
                        street = "124 Coral Avenue",
                        city = "Bikini Bottom",
                        state = "BB",
                        postalCode = "00001",
                        country = "Fictional"
                    },
                    phoneNumbers = new[] { "+1-555-0100" },
                    email = "spongebob@example.invalid"
                },
                new
                {
                    employeeId = "KK-002",
                    name = "Squidward Tentacles",
                    jobTitle = "Cashier",
                    department = "Front Counter",
                    yearsOfEmployment = 8,
                    employmentStatus = "FullTime",
                    achievements = new[]
                    {
                        "Cash register accuracy award",
                        "Completed customer service training"
                    },
                    salary = new { amount = 18.50m, currency = "USD", payFrequency = "Hourly" },
                    address = new
                    {
                        street = "126 Coral Avenue",
                        city = "Bikini Bottom",
                        state = "BB",
                        postalCode = "00001",
                        country = "Fictional"
                    },
                    phoneNumbers = new[] { "+1-555-0101" },
                    email = "squidward@example.invalid"
                },
                new
                {
                    employeeId = "KK-003",
                    name = "Eugene Krabs",
                    jobTitle = "Owner and Manager",
                    department = "Management",
                    yearsOfEmployment = 25,
                    employmentStatus = "FullTime",
                    achievements = new[]
                    {
                        "Founded the restaurant",
                        "Opened the first drive-through window",
                        "Reduced food waste by 12 percent"
                    },
                    salary = new { amount = 95000.00m, currency = "USD", payFrequency = "Annual" },
                    address = new
                    {
                        street = "1 Krusty Krab Plaza",
                        city = "Bikini Bottom",
                        state = "BB",
                        postalCode = "00001",
                        country = "Fictional"
                    },
                    phoneNumbers = new[] { "+1-555-0102", "+1-555-0103" },
                    email = "manager@example.invalid"
                }
            }
        });
    }

    [HttpGet("employees")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetEmployees()
    {
        return Get();
    }

    [HttpGet("competitors")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetCompetitors()
    {
        return Ok(new
        {
            source = "Krusty Krab competitor directory (fictional demo data)",
            competitors = new[]
            {
                new
                {
                    competitorId = "COMP-001",
                    name = "The Chum Bucket",
                    owner = "Sheldon Plankton",
                    specialty = "Experimental seafood alternatives",
                    estimatedDistanceMiles = 0.4,
                    threatLevel = "High",
                    knownAdvantages = new[] { "Aggressive advertising", "Experimental menu" }
                },
                new
                {
                    competitorId = "COMP-002",
                    name = "Goo Lagoon Grill",
                    owner = "Marina Fin",
                    specialty = "Beachside grilled seafood",
                    estimatedDistanceMiles = 2.1,
                    threatLevel = "Medium",
                    knownAdvantages = new[] { "Ocean view seating", "Seasonal menu" }
                },
                new
                {
                    competitorId = "COMP-003",
                    name = "Anchor Arms Cafe",
                    owner = "Captain Coral",
                    specialty = "Family diner fare",
                    estimatedDistanceMiles = 3.7,
                    threatLevel = "Low",
                    knownAdvantages = new[] { "Large portions", "Late-night hours" }
                }
            }
        });
    }

    [HttpGet("recipes")]
    [HttpGet("recipies")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetRecipes()
    {
        return Ok(new
        {
            source = "Krusty Krab recipe archive (fictional demo data)",
            recipes = new[]
            {
                new
                {
                    recipeId = "REC-001",
                    name = "Crabby Patty",
                    category = "Signature Sandwich",
                    preparationTimeMinutes = 8,
                    ingredients = new[]
                    {
                        "One toasted bun",
                        "One seasoned patty",
                        "Lettuce",
                        "Tomato",
                        "Cheese",
                        "Secret sauce"
                    },
                    instructions = new[]
                    {
                        "Toast the bun.",
                        "Cook the patty thoroughly.",
                        "Add toppings and secret sauce.",
                        "Serve immediately."
                    },
                    allergenNotes = new[] { "Contains wheat and dairy" }
                },
                new
                {
                    recipeId = "REC-002",
                    name = "Kelp Shake",
                    category = "Beverage",
                    preparationTimeMinutes = 3,
                    ingredients = new[] { "Chilled kelp blend", "Ice", "Vanilla foam" },
                    instructions = new[] { "Blend ingredients until smooth.", "Top with vanilla foam.", "Serve chilled." },
                    allergenNotes = Array.Empty<string>()
                }
            }
        });
    }

    [HttpGet("inventory")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetInventory()
    {
        return Ok(new
        {
            source = "Krusty Krab inventory system (fictional demo data)",
            items = new[]
            {
                new { item = "Patty blanks", category = "Food", quantity = 240, unit = "each", reorderThreshold = 60, status = "InStock" },
                new { item = "Sesame buns", category = "Food", quantity = 180, unit = "each", reorderThreshold = 50, status = "InStock" },
                new { item = "Kelp shakes", category = "Beverage", quantity = 42, unit = "servings", reorderThreshold = 48, status = "ReorderSoon" },
                new { item = "Paper crowns", category = "Supplies", quantity = 12, unit = "packs", reorderThreshold = 10, status = "InStock" }
            }
        });
    }

    [HttpGet("sales/daily")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetDailySales()
    {
        return Ok(new
        {
            date = DateTime.UtcNow.Date,
            currency = "USD",
            orders = 347,
            completedOrders = 339,
            cancelledOrders = 8,
            grossRevenue = 2847.65m,
            averageOrderValue = 8.20m,
            topSellingItem = "Crabby Patty"
        });
    }

    [HttpGet("customers")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetCustomers()
    {
        return Ok(new
        {
            source = "Customer loyalty directory (fictional demo data)",
            customers = new[]
            {
                new { customerId = "CUST-001", nickname = "The Regular", favoriteItem = "Crabby Patty", visitsThisMonth = 22, loyaltyTier = "Gold" },
                new { customerId = "CUST-002", nickname = "Jellyfishing Fan", favoriteItem = "Kelp Shake", visitsThisMonth = 8, loyaltyTier = "Silver" },
                new { customerId = "CUST-003", nickname = "Mystery Diner", favoriteItem = "Coral Bits", visitsThisMonth = 3, loyaltyTier = "Bronze" }
            }
        });
    }

    [HttpGet("locations")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetLocations()
    {
        return Ok(new
        {
            locations = new[]
            {
                new { locationId = "KK-MAIN", name = "Main Krusty Krab", address = "1 Krusty Krab Plaza, Bikini Bottom", status = "Open", seatingCapacity = 42 },
                new { locationId = "KK-DRIVE", name = "Krusty Krab Drive-Through", address = "2 Krusty Krab Plaza, Bikini Bottom", status = "Open", seatingCapacity = 0 },
                new { locationId = "KK-TRAIN", name = "Training Kitchen", address = "3 Krusty Krab Plaza, Bikini Bottom", status = "Maintenance", seatingCapacity = 12 }
            }
        });
    }

    [HttpGet("reviews")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetReviews()
    {
        return Ok(new
        {
            averageRating = 4.8,
            reviewCount = 1284,
            reviews = new[]
            {
                new { reviewId = "REV-1001", rating = 5, comment = "The service was faster than a jellyfish on espresso.", source = "Fictional Review Board" },
                new { reviewId = "REV-1002", rating = 4, comment = "Excellent sandwich. The napkins were mysteriously damp.", source = "Fictional Review Board" },
                new { reviewId = "REV-1003", rating = 5, comment = "Would visit again after my next boating lesson.", source = "Fictional Review Board" }
            }
        });
    }

    [HttpGet("inspections")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetInspections()
    {
        return Ok(new
        {
            latestInspectionDate = DateTime.UtcNow.Date.AddDays(-3),
            overallScore = 96,
            status = "Passed",
            inspector = "Bikini Bottom Food Safety Office",
            findings = new[]
            {
                new { area = "Kitchen temperature", score = 100, status = "Passed" },
                new { area = "Hand-washing stations", score = 98, status = "Passed" },
                new { area = "Ingredient labeling", score = 90, status = "FollowUpRecommended" }
            }
        });
    }

    [HttpGet("employee-of-the-month")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetEmployeeOfTheMonth()
    {
        return Ok(new
        {
            month = DateTime.UtcNow.ToString("yyyy-MM"),
            employeeId = "KK-001",
            employeeName = "SpongeBob SquarePants",
            award = "Employee of the Month",
            achievements = new[] { "Perfect attendance", "Fastest safe spatula handling", "Positive customer feedback" },
            bonus = new { amount = 25.00m, currency = "USD", type = "GiftCard" }
        });
    }

    [HttpGet("secret-formula/status")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetSecretFormulaStatus()
    {
        return Ok(new
        {
            classification = "TopSecret",
            formulaAvailable = true,
            lastAuditUtc = DateTime.UtcNow.AddDays(-2),
            authorizedCustodians = 2,
            storageStatus = "TripleLocked",
            note = "Ingredient details are intentionally not returned by this endpoint."
        });
    }

    [HttpGet("underwater-weather")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetUnderwaterWeather()
    {
        return Ok(new
        {
            location = "Bikini Bottom",
            observedAtUtc = DateTime.UtcNow,
            conditions = "Clear bubbles",
            temperatureFahrenheit = 72,
            currentSpeedKnots = 1.8,
            visibilityFeet = 85,
            jellyfishActivity = "Moderate",
            boatingAdvisory = "Use a licensed boat operator"
        });
    }

    [HttpGet("health/ingredients")]
    [Authorize(Policy = "ApiRead")]
    public IActionResult GetIngredientHealth()
    {
        return Ok(new
        {
            checkedAtUtc = DateTime.UtcNow,
            overallStatus = "Healthy",
            ingredients = new[]
            {
                new { ingredient = "Patty blanks", freshnessScore = 99, temperatureFahrenheit = 38, status = "Good" },
                new { ingredient = "Lettuce", freshnessScore = 94, temperatureFahrenheit = 39, status = "Good" },
                new { ingredient = "Tomatoes", freshnessScore = 87, temperatureFahrenheit = 41, status = "UseSoon" },
                new { ingredient = "Kelp blend", freshnessScore = 96, temperatureFahrenheit = 37, status = "Good" }
            }
        });
    }

    [HttpPost]
    [Authorize(Policy = "ApiWrite")]
    public IActionResult Post()
    {
        return Ok(new { message = "Write allowed." });
    }
}