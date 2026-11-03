using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FacebookPublisher.Infrastructure.Persistence;

/// <summary>Used only by "dotnet ef" tooling to create migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FacebookPublisher;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new AppDbContext(options);
    }
}
