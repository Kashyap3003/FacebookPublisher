namespace FacebookPublisher.Domain.Common;

/// <summary>
/// A record that belongs to exactly one application user. The persistence layer stamps the owner on insert, filters
/// every query by the current owner, and rejects writes to records of another owner.
/// </summary>
public interface IOwnedEntity
{
    Guid OwnerId { get; set; }
}
