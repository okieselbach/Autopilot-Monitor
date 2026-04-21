using AutopilotMonitor.Functions.Functions.Sessions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure helpers on <see cref="GetSessionSignalsFunction"/>. The HTTP-trigger
/// end needs a live runtime harness; this file covers the deterministic query-string
/// normalisation that decides how many rows we load.
/// </summary>
public class GetSessionSignalsFunctionTests
{
    [Fact]
    public void ParseMaxResults_defaults_when_query_param_missing()
    {
        var result = GetSessionSignalsFunction.ParseMaxResults("");

        Assert.Equal(GetSessionSignalsFunction.DefaultMaxResults, result);
    }

    [Fact]
    public void ParseMaxResults_defaults_when_query_param_is_garbage()
    {
        var result = GetSessionSignalsFunction.ParseMaxResults("?maxResults=banana");

        Assert.Equal(GetSessionSignalsFunction.DefaultMaxResults, result);
    }

    [Fact]
    public void ParseMaxResults_honours_valid_value_inside_range()
    {
        var result = GetSessionSignalsFunction.ParseMaxResults("?maxResults=250");

        Assert.Equal(250, result);
    }

    [Fact]
    public void ParseMaxResults_caps_at_MaxResultsCap()
    {
        var result = GetSessionSignalsFunction.ParseMaxResults("?maxResults=999999");

        Assert.Equal(GetSessionSignalsFunction.MaxResultsCap, result);
    }

    [Theory]
    [InlineData("?maxResults=0")]
    [InlineData("?maxResults=-5")]
    public void ParseMaxResults_floors_at_one_for_non_positive_values(string query)
    {
        var result = GetSessionSignalsFunction.ParseMaxResults(query);

        Assert.Equal(1, result);
    }

    [Fact]
    public void ParseMaxResults_handles_null_query_string()
    {
        var result = GetSessionSignalsFunction.ParseMaxResults(null!);

        Assert.Equal(GetSessionSignalsFunction.DefaultMaxResults, result);
    }
}
