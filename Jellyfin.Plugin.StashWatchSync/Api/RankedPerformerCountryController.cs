using System;
using System.Collections.Generic;
using System.Linq;
using JFToStashSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JFToStashSync.Api;

/// <summary>
/// Returns only birthplace country codes for Person IDs already shown in the prepared
/// performer rows. Does not scan the library, fetch external metadata or require Actor Plus.
/// </summary>
[ApiController]
[Route("JFToStashSync")]
[Authorize]
public sealed class RankedPerformerCountryController : ControllerBase
{
    private static readonly CountryCodeMapper CountryMapper = new();
    private readonly ILibraryManager _library;

    public RankedPerformerCountryController(ILibraryManager library)
    {
        _library = library;
    }

    [HttpGet("RankedPerformerBirthCountries")]
    public ActionResult<Dictionary<string, CountryResult>> GetBirthCountries([FromQuery] string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids)) return Ok(new Dictionary<string, CountryResult>());

        var parts = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 80) return BadRequest("Too many person IDs (maximum 80).");

        var result = new Dictionary<string, CountryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in parts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(text, out var guid) || guid == Guid.Empty) continue;
            if (_library.GetItemById(guid) is not Person person) continue;

            var birthplace = person.ProductionLocations is { Length: > 0 }
                ? string.Join(", ", person.ProductionLocations)
                : string.Empty;
            var iso2 = CountryMapper.BirthPlaceToIso2(birthplace);
            if (iso2 is not null)
            {
                result[guid.ToString("N")] = new CountryResult(iso2, birthplace);
            }
        }

        return Ok(result);
    }

    public sealed record CountryResult(string Country, string BirthPlace);
}
