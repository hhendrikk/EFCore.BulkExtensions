using System;
using System.Collections.Generic;
using System.Data.Common;

namespace EFCore.BulkExtensions
{
    public class BulkConfig
    {
        public bool PreserveInsertOrder { get; set; } = true;

        public bool SetOutputIdentity { get; set; }

        public int BatchSize { get; set; } = 2000;

        public int? NotifyAfter { get; set; }

        public int? BulkCopyTimeout { get; set; }

        public bool EnableStreaming { get; set; }

        public bool UseTempDB { get; set; }

        public bool UniqueTableNameTempDb { get; set; } = true;

        public string CustomDestinationTableName { get; set; }

        public bool TrackingEntities { get; set; }

        public bool WithHoldlock { get; set; } = true;

        public bool CalculateStats { get; set; }
        public StatsInfo StatsInfo { get; set; }
        public List<string> PropertiesToInclude { get; set; }
        public List<string> PropertiesToIncludeOnCompare { get; set; }
        public List<string> PropertiesToIncludeOnUpdate { get; set; }

        public List<string> PropertiesToExclude { get; set; }
        public List<string> PropertiesToExcludeOnCompare { get; set; }
        public List<string> PropertiesToExcludeOnUpdate { get; set; }

        public List<string> UpdateByProperties { get; set; }

        public bool EnableShadowProperties { get; set; } = false;
        public bool IncludeGraph { get; set; } = false;

        public int SRID { get; set; } = 4326; // Spatial Reference Identifier // https://docs.microsoft.com/en-us/sql/relational-databases/spatial/spatial-reference-identifiers-srids

        // since Microsoft.Data.SqlClient.SqlBulkCopyOptions is a superset of 
        // System.Data.SqlClient.SqlBulkCopyOptions, allow user to always specify
        // Microsoft.Data.SqlClient and we will convert it to the desired type
        public Microsoft.Data.SqlClient.SqlBulkCopyOptions SqlBulkCopyOptions { get; set; }

        public Func<DbConnection, DbConnection> UnderlyingConnection { get; set; }
        public Func<DbTransaction, DbTransaction> UnderlyingTransaction { get; set; }

        internal OperationType OperationType { get; set; }
    }

    public class StatsInfo
    {
        public int StatsNumberInserted { get; set; }

        public int StatsNumberUpdated { get; set; }

        public int StatsNumberDeleted { get; set; }
    }
}
