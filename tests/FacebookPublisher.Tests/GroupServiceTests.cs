using FacebookPublisher.Application.Automation;
using FacebookPublisher.Application.Common;
using FacebookPublisher.Application.Groups;
using FacebookPublisher.Infrastructure.Campaigns;
using Microsoft.Extensions.Logging.Abstractions;

namespace FacebookPublisher.Tests;

public class GroupServiceTests
{
    private readonly CampaignRunnerTests.TestDbFactory _db = new();
    private readonly CampaignRunnerTests.FakeAutomation _automation = new();
    private readonly CampaignExecutionTracker _tracker = new();

    private GroupService CreateService() => new(_db, _automation, _tracker, NullLogger<GroupService>.Instance);

    [Fact]
    public async Task Bulk_add_resolves_share_links_to_group_addresses_and_names()
    {
        _automation.ShareLinks["https://www.facebook.com/share/g/1FMskdPL2Z/"] =
            new ResolvedLink("https://www.facebook.com/groups/123456789/?mibextid=abc", "Local Makers");
        _automation.ShareLinks["https://www.facebook.com/share/g/1FEEdt983u/"] =
            new ResolvedLink("https://www.facebook.com/groups/cityevents/", "City Events");

        var result = await CreateService().AddBulkAsync("""
            https://www.facebook.com/share/g/1FMskdPL2Z/
            My name | https://www.facebook.com/share/g/1FEEdt983u/
            https://www.facebook.com/groups/plaingroup
            """);

        Assert.Equal(3, result.Added);
        Assert.Equal(2, result.Resolved.Count);
        Assert.Empty(result.Invalid);

        var groups = await CreateService().ListAsync();
        Assert.Contains(groups, g => g.Url == "https://www.facebook.com/groups/123456789" && g.Name == "Local Makers");
        Assert.Contains(groups, g => g.Url == "https://www.facebook.com/groups/cityevents" && g.Name == "My name");
        Assert.Contains(groups, g => g.Url == "https://www.facebook.com/groups/plaingroup");
    }

    [Fact]
    public async Task Share_link_to_an_already_listed_group_is_reported_as_duplicate()
    {
        _automation.ShareLinks["https://www.facebook.com/share/g/1FMskdPL2Z/"] =
            new ResolvedLink("https://www.facebook.com/groups/123456789/", "Local Makers");

        var result = await CreateService().AddBulkAsync("""
            https://www.facebook.com/groups/123456789
            https://www.facebook.com/share/g/1FMskdPL2Z/
            """);

        Assert.Equal(1, result.Added);
        Assert.Equal(["https://www.facebook.com/groups/123456789"], result.Duplicates);
    }

    [Fact]
    public async Task Share_links_are_reported_when_not_logged_in_and_browser_is_not_retried()
    {
        _automation.LoggedIn = false;

        var result = await CreateService().AddBulkAsync("""
            https://www.facebook.com/share/g/1FMskdPL2Z/
            https://www.facebook.com/share/g/1FEEdt983u/
            https://www.facebook.com/groups/plaingroup
            """);

        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Invalid.Count);
        Assert.All(result.Invalid, line => Assert.Contains("Log in from Settings", line));
        Assert.Single(_automation.ResolvedRequests);
    }

    [Fact]
    public async Task Share_link_that_does_not_lead_to_a_group_is_rejected()
    {
        // No mapping: the fake browser never reaches a group page.
        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            CreateService().AddAsync(new SaveGroupRequest(null, "https://www.facebook.com/share/g/1GtRXQEUEy/", null)));
        Assert.Contains("did not open a Facebook group", ex.Message);
    }

    [Fact]
    public async Task Share_links_are_not_resolved_while_a_campaign_is_running()
    {
        _tracker.Begin(Guid.NewGuid(), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            CreateService().AddAsync(new SaveGroupRequest(null, "https://www.facebook.com/share/g/1GtRXQEUEy/", null)));
        Assert.Contains("running campaign", ex.Message);
        Assert.Empty(_automation.ResolvedRequests);
    }
}
