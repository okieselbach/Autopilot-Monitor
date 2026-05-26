using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Backup;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure-function checks for <see cref="RestoreTablePreflightValidator"/>. The
/// preflight gates every <c>POST .../restore-row</c> request with no I/O — fail
/// fast before the heavy validator runs.
/// </summary>
public class RestoreTablePreflightValidatorTests
{
    private static readonly RestoreTablePreflightValidator Sut = new();

    [Fact]
    public void ValidateRowRequest_accepts_valid_preview()
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = "tenant1",
            RowKey = "rule1",
            Mode = RestoreRowMode.Preview,
        };

        Sut.ValidateRowRequest("20260522T040000Z_a1b2c3d4", req);   // no throw
    }

    [Fact]
    public void ValidateRowRequest_accepts_valid_commit_with_sha_and_etag()
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = "tenant1",
            RowKey = "rule1",
            Mode = RestoreRowMode.Commit,
            IfSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            IfCurrentETag = "\"0x8DABCDEF\"",
        };

        Sut.ValidateRowRequest("20260522T040000Z_a1b2c3d4", req);   // no throw
    }

    [Fact]
    public void ValidateRowRequest_accepts_commit_with_null_etag_when_row_was_missing()
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = "tenant1",
            RowKey = "rule1",
            Mode = RestoreRowMode.Commit,
            IfSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            IfCurrentETag = null,
        };

        Sut.ValidateRowRequest("20260522T040000Z_a1b2c3d4", req);   // no throw — Add-path
    }

    [Fact]
    public void ValidateRowRequest_rejects_empty_backupId()
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = "p",
            RowKey = "r",
            Mode = RestoreRowMode.Preview,
        };
        var ex = Assert.Throws<BackupTerminalException>(() => Sut.ValidateRowRequest("", req));
        Assert.Equal("InvalidBackupId", ex.Code);
    }

    [Fact]
    public void ValidateRowRequest_rejects_table_not_in_catalog()
    {
        var req = new RestoreRowRequest
        {
            TableName = "RandomOtherTable",
            PartitionKey = "p",
            RowKey = "r",
            Mode = RestoreRowMode.Preview,
        };
        var ex = Assert.Throws<BackupTerminalException>(() => Sut.ValidateRowRequest("backup1", req));
        Assert.Equal("InvalidTable", ex.Code);
    }

    [Fact]
    public void ValidateRowRequest_rejects_null_partition_key()
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = null!,
            RowKey = "r",
            Mode = RestoreRowMode.Preview,
        };
        var ex = Assert.Throws<BackupTerminalException>(() => Sut.ValidateRowRequest("backup1", req));
        Assert.Equal("InvalidKeys", ex.Code);
    }

    [Fact]
    public void ValidateRowRequest_commit_requires_ifSha256()
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = "p",
            RowKey = "r",
            Mode = RestoreRowMode.Commit,
            IfSha256 = null,
        };
        var ex = Assert.Throws<BackupTerminalException>(() => Sut.ValidateRowRequest("backup1", req));
        Assert.Equal("MissingPrecondition", ex.Code);
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]  // uppercase rejected
    [InlineData("0123456789abcdef")]                                                  // too short
    public void ValidateRowRequest_commit_requires_lowercase_64_hex(string sha)
    {
        var req = new RestoreRowRequest
        {
            TableName = Constants.TableNames.AnalyzeRules,
            PartitionKey = "p",
            RowKey = "r",
            Mode = RestoreRowMode.Commit,
            IfSha256 = sha,
        };
        var ex = Assert.Throws<BackupTerminalException>(() => Sut.ValidateRowRequest("backup1", req));
        Assert.Equal("MissingPrecondition", ex.Code);
    }

    [Theory]
    [InlineData(nameof(Constants.TableNames.GlobalAdmins), true)]
    [InlineData(nameof(Constants.TableNames.TenantAdmins), true)]
    [InlineData(nameof(Constants.TableNames.McpUsers), true)]
    [InlineData(nameof(Constants.TableNames.AnalyzeRules), false)]
    [InlineData(nameof(Constants.TableNames.Feedback), false)]
    public void IsAuthTable_flags_only_three_security_tables(string tableNameField, bool expected)
    {
        var tableName = typeof(Constants.TableNames).GetField(tableNameField)!.GetValue(null) as string;
        Assert.Equal(expected, Sut.IsAuthTable(tableName!));
    }
}
