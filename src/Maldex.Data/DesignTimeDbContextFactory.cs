using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Maldex.Data;

/// <summary>Used by dotnet-ef only. Reads MALDEX_DESIGN_CONNSTR; the fallback is a dummy (migrations generation never connects).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MaldexDbContext>
{
    public MaldexDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("MALDEX_DESIGN_CONNSTR")
                 ?? "Server=localhost,1433;Database=maldex_design;User Id=sa;Password=Design-Only-1!;TrustServerCertificate=True";
        return new MaldexDbContext(new DbContextOptionsBuilder<MaldexDbContext>().UseSqlServer(cs).Options);
    }
}
