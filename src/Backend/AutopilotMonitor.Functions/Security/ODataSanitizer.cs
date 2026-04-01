namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// OData filter value sanitization for Azure Table Storage queries.
    /// Azure.Data.Tables SDK does not support parameterized queries,
    /// so string values interpolated into OData filters must be escaped.
    /// </summary>
    public static class ODataSanitizer
    {
        /// <summary>
        /// Escapes a string value for safe use inside OData single-quoted literals.
        /// In OData, a single quote inside a string literal is represented as two single quotes.
        /// </summary>
        public static string EscapeValue(string value)
        {
            return value.Replace("'", "''");
        }
    }
}
