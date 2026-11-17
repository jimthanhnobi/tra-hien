using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;

namespace TeaPremium.Infrastructure.Persistence;

// 03-database §8 uses yyyyMMddHHmm; the committed InitialPlatform history already
// uses this format. EF's default parser only recognizes yyyyMMddHHmmss IDs.
// Preserve historical IDs and also accept ordinary EF-generated 14-digit IDs.
#pragma warning disable EF1001 // Narrow parser adapter over EF's default generator; verified by real up/down migrations.
public sealed partial class RepositoryMigrationIds : MigrationsIdGenerator
{
    public override bool IsValidId(string value) => RepositoryId().IsMatch(value) || base.IsValidId(value);
    public override string GetName(string id) => RepositoryId().IsMatch(id) ? id[13..] : base.GetName(id);
    public override string GenerateId(string name) => base.GenerateId(name).Remove(12, 2);

    [GeneratedRegex("^[0-9]{12}_.+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryId();
}
