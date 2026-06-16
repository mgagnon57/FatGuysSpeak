using System.Security.Claims;
using System.Text.RegularExpressions;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Route("api/servers/{serverId}/wordfilter")]
[Authorize]
public class WordFiltersController(AppDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<ServerMember?> GetMemberAsync(int serverId) =>
        await db.ServerMembers.FindAsync(serverId, UserId);

    [HttpGet]
    public async Task<ActionResult<List<WordFilterDto>>> GetFilters(int serverId)
    {
        var member = await GetMemberAsync(serverId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var filters = await db.WordFilters
            .Where(f => f.ServerId == serverId)
            .OrderBy(f => f.Pattern)
            .Select(f => new WordFilterDto(f.Id, f.Pattern, f.CreatedAt, f.Severity, f.CaseSensitive))
            .ToListAsync();

        return filters;
    }

    [HttpPost]
    public async Task<ActionResult<WordFilterDto>> AddFilter(int serverId, AddWordFilterRequest req)
    {
        var member = await GetMemberAsync(serverId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var pattern = req.Pattern.Trim();
        if (string.IsNullOrEmpty(pattern) || pattern.Length > 100)
            return BadRequest("Pattern must be 1–100 characters.");

        var count = await db.WordFilters.CountAsync(f => f.ServerId == serverId);
        if (count >= 200) return BadRequest("Maximum of 200 filter patterns per server.");

        var existing = await db.WordFilters.AnyAsync(f =>
            f.ServerId == serverId && f.Pattern.ToLower() == pattern.ToLower());
        if (existing) return Conflict("Pattern already exists.");

        var filter = new WordFilter { ServerId = serverId, Pattern = pattern, Severity = req.Severity, CaseSensitive = req.CaseSensitive };
        db.WordFilters.Add(filter);
        await db.SaveChangesAsync();

        return Ok(new WordFilterDto(filter.Id, filter.Pattern, filter.CreatedAt, filter.Severity, filter.CaseSensitive));
    }

    [HttpDelete("{filterId}")]
    public async Task<IActionResult> RemoveFilter(int serverId, int filterId)
    {
        var member = await GetMemberAsync(serverId);
        if (member is null || member.Role < ServerRole.Admin) return Forbid();

        var filter = await db.WordFilters.FindAsync(filterId);
        if (filter is null || filter.ServerId != serverId) return NotFound();

        db.WordFilters.Remove(filter);
        await db.SaveChangesAsync();
        return NoContent();
    }

    public record WordFilterResult(string FilteredContent, WordFilterSeverity? MaxSeverity, string? MatchedPattern);

    public static WordFilterResult Apply(string content, List<WordFilter> filters)
    {
        WordFilterSeverity? maxSeverity = null;
        string? matchedPattern = null;
        var result = content;

        foreach (var filter in filters)
        {
            var escaped = Regex.Escape(filter.Pattern);
            var pat = filter.Pattern.Contains(' ') ? escaped : $@"\b{escaped}\b";
            var opts = filter.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

            bool matched = Regex.IsMatch(result, pat, opts);
            if (!matched && !filter.CaseSensitive)
            {
                var normalized = LeetSpeakNormalizer.Normalize(content);
                matched = Regex.IsMatch(normalized, $@"\b{escaped}\b", RegexOptions.IgnoreCase);
            }

            if (!matched) continue;

            if (maxSeverity is null || filter.Severity > maxSeverity)
            {
                maxSeverity = filter.Severity;
                matchedPattern = filter.Pattern;
            }

            result = Regex.Replace(result, pat, m => new string('*', m.Length), opts);
        }

        return new WordFilterResult(result, maxSeverity, matchedPattern);
    }
}
