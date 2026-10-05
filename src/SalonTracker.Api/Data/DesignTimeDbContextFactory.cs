using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SalonTracker.Api.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without starting the app.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=salon_tracker_design")
            .UseSnakeCaseNamingConvention()
            .Options);
}
