using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dmart.Tests.Infrastructure;

// Tests that successfully create a user via /user/create end up triggering
// the resource_folders_creation plugin, which materializes
//   personal/people/{shortname}             (folder)
//   personal/people/{shortname}/notifications
//   personal/people/{shortname}/private
//   personal/people/{shortname}/protected
//   personal/people/{shortname}/public
//   personal/people/{shortname}/inbox
// each with owner_shortname = the new user. A naive
// `users.DeleteAsync(shortname)` then trips the
// entries.owner_shortname → users.shortname FK.
//
// This helper purges everything the plugin (or future plugins) creates
// owned by that user across ALL FK-referencing tables, then deletes the
// user. Use it instead of `users.DeleteAsync(shortname)` whenever the
// test went through /user/create successfully.
public static class TestUserCleanup
{
    public static async Task DeleteUserAndOwnedAsync(IServiceProvider sp, string shortname)
    {
        var db = sp.GetRequiredService<Db>();
        var users = sp.GetRequiredService<UserRepository>();

        await using (var conn = await db.OpenAsync())
        {
            // Order: drop FK-bearing rows in entries/attachments/roles/
            // permissions/spaces first (everything that REFERENCES
            // users.shortname), then the user row itself. Single connection,
            // single round-trip per table — fine for tests.
            foreach (var table in new[] { "attachments", "entries", "spaces", "roles", "permissions" })
            {
                await using var cmd = new NpgsqlCommand(
                    $"DELETE FROM {table} WHERE owner_shortname = $1", conn);
                cmd.Parameters.Add(new() { Value = shortname });
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await users.DeleteAsync(shortname);
    }
}
