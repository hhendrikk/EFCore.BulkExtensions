using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EFCore.BulkExtensions.SqlAdapters;

namespace EFCore.BulkExtensions
{
    public class TableInfo
    {
        public string Schema { get; set; }
        public string SchemaFormated => Schema != null ? $"[{Schema}]." : "";
        public string TableName { get; set; }
        public string FullTableName => $"{SchemaFormated}[{TableName}]";
        public Dictionary<string, string> PrimaryKeys { get; set; }
        public bool HasSinglePrimaryKey { get; set; }
        public bool UpdateByPropertiesAreNullable { get; set; }

        protected string TempDBPrefix => BulkConfig.UseTempDB ? "#" : "";
        public string TempTableSufix { get; set; }
        public string TempTableName => $"{TableName}{TempTableSufix}";
        public string FullTempTableName => $"{SchemaFormated}[{TempDBPrefix}{TempTableName}]";
        public string FullTempOutputTableName => $"{SchemaFormated}[{TempDBPrefix}{TempTableName}Output]";

        public bool CreatedOutputTable => BulkConfig.SetOutputIdentity || BulkConfig.CalculateStats;

        public bool InsertToTempTable { get; set; }
        public string IdentityColumnName { get; set; }
        public bool HasIdentity => IdentityColumnName != null;
        public bool HasOwnedTypes { get; set; }
        public bool HasAbstractList { get; set; }
        public bool ColumnNameContainsSquareBracket { get; set; }
        public bool LoadOnlyPKColumn { get; set; }
        public bool HasSpatialType { get; set; }
        public int NumberOfEntities { get; set; }

        public BulkConfig BulkConfig { get; set; }
        public Dictionary<string, string> OutputPropertyColumnNamesDict { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> PropertyColumnNamesDict { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ColumnNamesTypesDict { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> PropertyColumnNamesCompareDict { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> PropertyColumnNamesUpdateDict { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, FastProperty> FastPropertyDict { get; set; } = new Dictionary<string, FastProperty>();
        public Dictionary<string, INavigation> AllNavigationsDictionary { get; private set; }
        public Dictionary<string, INavigation> OwnedTypesDict { get; set; } = new Dictionary<string, INavigation>();
        public HashSet<string> ShadowProperties { get; set; } = new HashSet<string>();
        public Dictionary<string, ValueConverter> ConvertibleProperties { get; set; } = new Dictionary<string, ValueConverter>();
        public string TimeStampOutColumnType => "varbinary(8)";
        public string TimeStampColumnName { get; set; }

        public static TableInfo CreateInstance<T>(DbContext context, IList<T> entities, OperationType operationType, BulkConfig bulkConfig)
        {
            return CreateInstance<T>(context, typeof(T), entities, operationType, bulkConfig);
        }

        public static TableInfo CreateInstance(DbContext context, Type type, IList<object> entities, OperationType operationType, BulkConfig bulkConfig)
        {
            return CreateInstance<object>(context, type, entities, operationType, bulkConfig);
        }

        private static TableInfo CreateInstance<T>(DbContext context, Type type, IList<T> entities, OperationType operationType, BulkConfig bulkConfig)
        {
            var tableInfo = new TableInfo
            {
                NumberOfEntities = entities.Count,
                BulkConfig = bulkConfig ?? new BulkConfig() { }
            };
            tableInfo.BulkConfig.OperationType = operationType;

            bool isExplicitTransaction = context.Database.GetDbConnection().State == ConnectionState.Open;
            if (tableInfo.BulkConfig.UseTempDB == true && !isExplicitTransaction && (operationType != OperationType.Insert || tableInfo.BulkConfig.SetOutputIdentity))
            {
                throw new InvalidOperationException("UseTempDB when set then BulkOperation has to be inside Transaction. More info in README of the library in GitHub.");
                // Otherwise throws exception: 'Cannot access destination table' (gets Dropped too early because transaction ends before operation is finished)
            }

            var isDeleteOperation = operationType == OperationType.Delete;
            tableInfo.LoadData<T>(context, type, entities, isDeleteOperation);
            return tableInfo;
        }

        #region Main
        public void LoadData<T>(DbContext context, IList<T> entities, bool loadOnlyPKColumn)
        {
            LoadData<T>(context, typeof(T), entities, loadOnlyPKColumn);
        }

        public void LoadData(DbContext context, Type type, IList<object> entities, bool loadOnlyPKColumn)
        {
            LoadData<object>(context, type, entities, loadOnlyPKColumn);
        }

        private void LoadData<T>(DbContext context, Type type, IList<T> entities, bool loadOnlyPKColumn)
        {
            LoadOnlyPKColumn = loadOnlyPKColumn;
            var entityType = context.Model.FindEntityType(type);
            if (entityType == null)
            {
                type = entities[0].GetType();
                entityType = context.Model.FindEntityType(type);
                HasAbstractList = true;
            }
            if (entityType == null)
                throw new InvalidOperationException($"DbContext does not contain EntitySet for Type: { type.Name }");

            //var relationalData = entityType.Relational(); relationalData.Schema relationalData.TableName // DEPRECATED in Core3.0
            bool isSqlServer = context.Database.ProviderName.EndsWith(DbServer.SqlServer.ToString());
            string defaultSchema = isSqlServer ? "dbo" : null;

            string customSchema = null;
            string customTableName = null;
            if (BulkConfig.CustomDestinationTableName != null)
            {
                customTableName = BulkConfig.CustomDestinationTableName;
                if (customTableName.Contains('.'))
                {
                    var tableNameSplitList = BulkConfig.CustomDestinationTableName.Split('.');
                    customSchema = tableNameSplitList[0];
                    customTableName = tableNameSplitList[1];
                }
            }
            Schema = customSchema ?? entityType.GetSchema() ?? defaultSchema;
            TableName = customTableName ?? entityType.GetTableName();

            TempTableSufix = "Temp";

            if (!BulkConfig.UseTempDB || BulkConfig.UniqueTableNameTempDb)
            {
                TempTableSufix += Guid.NewGuid().ToString().Substring(0, 8); // 8 chars of Guid as tableNameSufix to avoid same name collision with other tables
            }

            bool AreSpecifiedUpdateByProperties = BulkConfig.UpdateByProperties?.Count() > 0;
            var primaryKeys = entityType.FindPrimaryKey()?.Properties?.ToDictionary(a => a.Name, b => b.GetColumnName());

            HasSinglePrimaryKey = primaryKeys?.Count == 1;
            PrimaryKeys = AreSpecifiedUpdateByProperties ? BulkConfig.UpdateByProperties.ToDictionary(a => a, b => b) : primaryKeys;

            var allProperties = entityType.GetProperties().AsEnumerable();

            ColumnNamesTypesDict = allProperties.ToDictionary(a => a.GetColumnName(), a => a.GetColumnType());

            // load all derived type properties
            if (entityType.IsAbstract())
            {
                var extendedAllProperties = allProperties.ToList();
                foreach (var derived in entityType.GetDirectlyDerivedTypes())
                {
                    extendedAllProperties.AddRange(derived.GetProperties());
                }

                allProperties = extendedAllProperties.Distinct();
            }

            var navigations = entityType.GetNavigations();
            AllNavigationsDictionary = navigations.ToDictionary(nav => nav.Name, nav => nav);

            var ownedTypes = navigations.Where(a => a.GetTargetType().IsOwned());
            HasOwnedTypes = ownedTypes.Any();
            OwnedTypesDict = ownedTypes.ToDictionary(a => a.Name, a => a);

            IdentityColumnName = allProperties.SingleOrDefault(a => a.IsPrimaryKey() &&
                                                                     (a.ClrType.Name.StartsWith("Byte") ||
                                                                      a.ClrType.Name.StartsWith("SByte") ||
                                                                      a.ClrType.Name.StartsWith("Int") ||
                                                                      a.ClrType.Name.StartsWith("UInt") ||
                                                                      (isSqlServer && a.ClrType.Name.StartsWith("Decimal"))) &&
                                                                    !a.ClrType.Name.EndsWith("[]") && 
                                                                    a.ValueGenerated == ValueGenerated.OnAdd
                                                              )?.GetColumnName(); // ValueGenerated equals OnAdd even for nonIdentity column like Guid so we only type int as second condition

            // timestamp/row version properties are only set by the Db, the property has a [Timestamp] Attribute or is configured in FluentAPI with .IsRowVersion()
            // They can be identified by the columne type "timestamp" or .IsConcurrencyToken in combination with .ValueGenerated == ValueGenerated.OnAddOrUpdate
            string timestampDbTypeName = nameof(TimestampAttribute).Replace("Attribute", "").ToLower(); // = "timestamp";
            var timeStampProperties = allProperties.Where(a => (a.IsConcurrencyToken && a.ValueGenerated == ValueGenerated.OnAddOrUpdate) || a.GetColumnType() == timestampDbTypeName);
            TimeStampColumnName = timeStampProperties.FirstOrDefault()?.GetColumnName(); // can be only One
            var allPropertiesExceptTimeStamp = allProperties.Except(timeStampProperties);
            var properties = allPropertiesExceptTimeStamp.Where(a => a.GetComputedColumnSql() == null);
            var propertiesOnCompare = allPropertiesExceptTimeStamp.Where(a => a.GetComputedColumnSql() == null);
            var propertiesOnUpdate = allPropertiesExceptTimeStamp.Where(a => a.GetComputedColumnSql() == null);

            // TimeStamp prop. is last column in OutputTable since it is added later with varbinary(8) type in which Output can be inserted
            OutputPropertyColumnNamesDict = allPropertiesExceptTimeStamp.Concat(timeStampProperties).ToDictionary(a => a.Name, b => b.GetColumnName().Replace("]", "]]")); // square brackets have to be escaped
            ColumnNameContainsSquareBracket = allPropertiesExceptTimeStamp.Concat(timeStampProperties).Any(a => a.GetColumnName().Contains("]"));

            bool AreSpecifiedPropertiesToInclude = BulkConfig.PropertiesToInclude?.Count() > 0;
            bool AreSpecifiedPropertiesToExclude = BulkConfig.PropertiesToExclude?.Count() > 0;

            bool AreSpecifiedPropertiesToIncludeOnCompare = BulkConfig.PropertiesToIncludeOnCompare?.Count() > 0;
            bool AreSpecifiedPropertiesToExcludeOnCompare = BulkConfig.PropertiesToExcludeOnCompare?.Count() > 0;

            bool AreSpecifiedPropertiesToIncludeOnUpdate = BulkConfig.PropertiesToIncludeOnUpdate?.Count() > 0;
            bool AreSpecifiedPropertiesToExcludeOnUpdate = BulkConfig.PropertiesToExcludeOnUpdate?.Count() > 0;

            if (AreSpecifiedPropertiesToInclude)
            {
                if (AreSpecifiedUpdateByProperties) // Adds UpdateByProperties to PropertyToInclude if they are not already explicitly listed
                {
                    foreach (var updateByProperty in BulkConfig.UpdateByProperties)
                    {
                        if (!BulkConfig.PropertiesToInclude.Contains(updateByProperty))
                        {
                            BulkConfig.PropertiesToInclude.Add(updateByProperty);
                        }
                    }
                }
                else // Adds PrimaryKeys to PropertyToInclude if they are not already explicitly listed
                {
                    foreach (var primaryKey in PrimaryKeys)
                    {
                        if (!BulkConfig.PropertiesToInclude.Contains(primaryKey.Key))
                        {
                            BulkConfig.PropertiesToInclude.Add(primaryKey.Key);
                        }
                    }
                }
            }

            foreach (var property in allProperties)
            {
                if (property.PropertyInfo != null) // skip Shadow Property
                {
                    FastPropertyDict.Add(property.Name, new FastProperty(property.PropertyInfo));
                }
            }

            UpdateByPropertiesAreNullable = properties.Any(a => PrimaryKeys != null && PrimaryKeys.ContainsKey(a.Name) && a.IsNullable);

            if (AreSpecifiedPropertiesToInclude || AreSpecifiedPropertiesToExclude)
            {
                if (AreSpecifiedPropertiesToInclude && AreSpecifiedPropertiesToExclude)
                {
                    throw new MultiplePropertyListSetException(nameof(BulkConfig.PropertiesToInclude), nameof(BulkConfig.PropertiesToExclude));
                }  
                if (AreSpecifiedPropertiesToInclude)
                {
                    properties = properties.Where(a => BulkConfig.PropertiesToInclude.Contains(a.Name));
                    ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToInclude, nameof(BulkConfig.PropertiesToInclude));
                }
                if (AreSpecifiedPropertiesToExclude)
                {
                    properties = properties.Where(a => !BulkConfig.PropertiesToExclude.Contains(a.Name));
                    ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToExclude, nameof(BulkConfig.PropertiesToExclude));
                }
            }

            if (AreSpecifiedPropertiesToIncludeOnCompare || AreSpecifiedPropertiesToExcludeOnCompare)
            {
                if (AreSpecifiedPropertiesToIncludeOnCompare && AreSpecifiedPropertiesToExcludeOnCompare)
                {
                    throw new MultiplePropertyListSetException(nameof(BulkConfig.PropertiesToIncludeOnCompare), nameof(BulkConfig.PropertiesToExcludeOnCompare));
                }
                if (AreSpecifiedPropertiesToIncludeOnCompare)
                {
                    propertiesOnCompare = propertiesOnCompare.Where(a => BulkConfig.PropertiesToIncludeOnCompare.Contains(a.Name));
                    ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToIncludeOnCompare, nameof(BulkConfig.PropertiesToIncludeOnCompare));
                }
                if (AreSpecifiedPropertiesToExcludeOnCompare)
                {
                    propertiesOnCompare = propertiesOnCompare.Where(a => !BulkConfig.PropertiesToExcludeOnCompare.Contains(a.Name));
                    ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToExcludeOnCompare, nameof(BulkConfig.PropertiesToExcludeOnCompare));
                }
            }
            else
            {
                propertiesOnCompare = properties;
            }
            if (AreSpecifiedPropertiesToIncludeOnUpdate || AreSpecifiedPropertiesToExcludeOnUpdate)
            {
                if (AreSpecifiedPropertiesToIncludeOnUpdate && AreSpecifiedPropertiesToExcludeOnUpdate)
                {
                    throw new MultiplePropertyListSetException(nameof(BulkConfig.PropertiesToIncludeOnUpdate), nameof(BulkConfig.PropertiesToExcludeOnUpdate));
                }
                if (AreSpecifiedPropertiesToIncludeOnUpdate)
                {
                    propertiesOnUpdate = propertiesOnUpdate.Where(a => BulkConfig.PropertiesToIncludeOnUpdate.Contains(a.Name));
                    ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToIncludeOnUpdate, nameof(BulkConfig.PropertiesToIncludeOnUpdate));
                }
                if (AreSpecifiedPropertiesToExcludeOnUpdate)
                {
                    propertiesOnUpdate = propertiesOnUpdate.Where(a => !BulkConfig.PropertiesToExcludeOnUpdate.Contains(a.Name));
                    ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToExcludeOnUpdate, nameof(BulkConfig.PropertiesToExcludeOnUpdate));
                }
            }
            else
            {
                propertiesOnUpdate = properties;

                if (BulkConfig.UpdateByProperties != null) // to remove NonIdentity PK like Guid from SET ID = ID, ...
                {
                    propertiesOnUpdate = propertiesOnUpdate.Where(a => !BulkConfig.UpdateByProperties.Contains(a.Name));
                }
                else if (primaryKeys != null)
                {
                    propertiesOnUpdate = propertiesOnUpdate.Where(a => !primaryKeys.ContainsKey(a.Name));
                }
            }

            PropertyColumnNamesCompareDict = propertiesOnCompare.ToDictionary(a => a.Name, b => b.GetColumnName().Replace("]", "]]"));
            PropertyColumnNamesUpdateDict = propertiesOnUpdate.ToDictionary(a => a.Name, b => b.GetColumnName().Replace("]", "]]"));

            if (loadOnlyPKColumn)
            {
                PropertyColumnNamesDict = properties.Where(a => PrimaryKeys.ContainsKey(a.Name)).ToDictionary(a => a.Name, b => b.GetColumnName().Replace("]", "]]"));
            }
            else
            {
                PropertyColumnNamesDict = properties.ToDictionary(a => a.Name, b => b.GetColumnName().Replace("]", "]]"));
                ShadowProperties = new HashSet<string>(properties.Where(p => p.IsShadowProperty() && !p.IsForeignKey()).Select(p => p.GetColumnName()));
                foreach (var property in properties)
                {
                    var converter = property.GetTypeMapping().Converter;

                    if (converter is null)
                    {
                        continue;
                    }

                    var columnName = property.GetColumnName();
                    ConvertibleProperties.Add(columnName, converter);
                }

                foreach (var navigation in entityType.GetNavigations().Where(x => !x.IsCollection() && !x.GetTargetType().IsOwned()))
                {
                    FastPropertyDict.Add(navigation.Name, new FastProperty(navigation.PropertyInfo));
                }

                if (HasOwnedTypes)  // Support owned entity property update. TODO: Optimize
                {
                    foreach (var navgationProperty in ownedTypes)
                    {
                        var property = navgationProperty.PropertyInfo;
                        FastPropertyDict.Add(property.Name, new FastProperty(property));

                        Type navOwnedType = type.Assembly.GetType(property.PropertyType.FullName);
                        var ownedEntityType = context.Model.FindEntityType(property.PropertyType);
                        if (ownedEntityType == null) // when entity has more then one ownedType (e.g. Address HomeAddress, Address WorkAddress) or one ownedType is in multiple Entities like Audit is usually.
                        {
                            ownedEntityType = context.Model.GetEntityTypes().SingleOrDefault(a => a.DefiningNavigationName == property.Name && a.DefiningEntityType.Name == entityType.Name);
                        }
                        var ownedEntityProperties = ownedEntityType.GetProperties().ToList();
                        var ownedEntityPropertyNameColumnNameDict = new Dictionary<string, string>();

                        foreach (var ownedEntityProperty in ownedEntityProperties)
                        {
                            if (!ownedEntityProperty.IsPrimaryKey())
                            {
                                string columnName = ownedEntityProperty.GetColumnName();
                                ownedEntityPropertyNameColumnNameDict.Add(ownedEntityProperty.Name, columnName);
                                var ownedEntityPropertyFullName = property.Name + "_" + ownedEntityProperty.Name;
                                if (!FastPropertyDict.ContainsKey(ownedEntityPropertyFullName))
                                {
                                    FastPropertyDict.Add(ownedEntityPropertyFullName, new FastProperty(ownedEntityProperty.PropertyInfo));
                                }
                            }

                            var converter = ownedEntityProperty.GetValueConverter();
                            if (converter != null)
                            {
                                ConvertibleProperties.Add($"{navgationProperty.Name}_{ownedEntityProperty.Name}", converter);
                            }
                        }
                        var ownedProperties = property.PropertyType.GetProperties();
                        foreach (var ownedProperty in ownedProperties)
                        {
                            if (ownedEntityPropertyNameColumnNameDict.ContainsKey(ownedProperty.Name))
                            {
                                string columnName = ownedEntityPropertyNameColumnNameDict[ownedProperty.Name];
                                var ownedPropertyType = Nullable.GetUnderlyingType(ownedProperty.PropertyType) ?? ownedProperty.PropertyType;

                                bool doAddProperty = true;
                                if (AreSpecifiedPropertiesToInclude && !BulkConfig.PropertiesToInclude.Contains(columnName))
                                {
                                    doAddProperty = false;
                                }
                                if (AreSpecifiedPropertiesToExclude && BulkConfig.PropertiesToExclude.Contains(columnName))
                                {
                                    doAddProperty = false;
                                }

                                if (doAddProperty)
                                {
                                    PropertyColumnNamesDict.Add(property.Name + "." + ownedProperty.Name, columnName);
                                    OutputPropertyColumnNamesDict.Add(property.Name + "." + ownedProperty.Name, columnName);
                                }
                            }
                        }
                    }
                }
            }
        }

        protected void ValidateSpecifiedPropertiesList(List<string> specifiedPropertiesList, string specifiedPropertiesListName)
        {
            foreach (var configSpecifiedPropertyName in specifiedPropertiesList)
            {
                if (!FastPropertyDict.Any(a => a.Key == configSpecifiedPropertyName))
                {
                    throw new InvalidOperationException($"PropertyName '{configSpecifiedPropertyName}' specified in '{specifiedPropertiesListName}' not found in Properties.");
                }
            }
        }

        /// <summary>
        /// Supports <see cref="System.Data.SqlClient.SqlBulkCopy"/>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sqlBulkCopy"></param>
        /// <param name="entities"></param>
        /// <param name="setColumnMapping"></param>
        /// <param name="progress"></param>
        public void SetSqlBulkCopyConfig<T>(System.Data.SqlClient.SqlBulkCopy sqlBulkCopy, IList<T> entities, bool setColumnMapping, Action<decimal> progress)
        {
            sqlBulkCopy.DestinationTableName = InsertToTempTable ? FullTempTableName : FullTableName;
            sqlBulkCopy.BatchSize = BulkConfig.BatchSize;
            sqlBulkCopy.NotifyAfter = BulkConfig.NotifyAfter ?? BulkConfig.BatchSize;
            sqlBulkCopy.SqlRowsCopied += (sender, e) =>
            {
                progress?.Invoke(ProgressHelper.GetProgress(entities.Count, e.RowsCopied)); // round to 4 decimal places
            };
            sqlBulkCopy.BulkCopyTimeout = BulkConfig.BulkCopyTimeout ?? sqlBulkCopy.BulkCopyTimeout;
            sqlBulkCopy.EnableStreaming = BulkConfig.EnableStreaming;

            if (setColumnMapping)
            {
                foreach (var element in PropertyColumnNamesDict)
                {
                    sqlBulkCopy.ColumnMappings.Add(element.Key, element.Value);
                }
            }
        }

        /// <summary>
        /// Supports <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sqlBulkCopy"></param>
        /// <param name="entities"></param>
        /// <param name="setColumnMapping"></param>
        /// <param name="progress"></param>
        public void SetSqlBulkCopyConfig<T>(Microsoft.Data.SqlClient.SqlBulkCopy sqlBulkCopy, IList<T> entities, bool setColumnMapping, Action<decimal> progress)
        {
            sqlBulkCopy.DestinationTableName = InsertToTempTable ? FullTempTableName : FullTableName;
            sqlBulkCopy.BatchSize = BulkConfig.BatchSize;
            sqlBulkCopy.NotifyAfter = BulkConfig.NotifyAfter ?? BulkConfig.BatchSize;
            sqlBulkCopy.SqlRowsCopied += (sender, e) =>
            {
                progress?.Invoke(ProgressHelper.GetProgress(entities.Count, e.RowsCopied)); // round to 4 decimal places
            };
            sqlBulkCopy.BulkCopyTimeout = BulkConfig.BulkCopyTimeout ?? sqlBulkCopy.BulkCopyTimeout;
            sqlBulkCopy.EnableStreaming = BulkConfig.EnableStreaming;

            if (setColumnMapping)
            {
                foreach (var element in PropertyColumnNamesDict)
                {
                    sqlBulkCopy.ColumnMappings.Add(element.Key, element.Value);
                }
            }
        }
        #endregion

        #region SqlCommands
        public void CheckHasIdentity(DbContext context) // No longer used
        {
            context.Database.OpenConnection();
            try
            {
                var sqlConnection = context.Database.GetDbConnection();
                var currentTransaction = context.Database.CurrentTransaction;

                using (var command = sqlConnection.CreateCommand())
                {
                    if (currentTransaction != null)
                        command.Transaction = currentTransaction.GetDbTransaction();
                    command.CommandText = SqlQueryBuilder.SelectIdentityColumnName(TableName, Schema);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.HasRows)
                        {
                            while (reader.Read())
                            {
                                IdentityColumnName = reader.GetString(0);
                            }
                        }
                    }
                }
            }
            finally
            {
                context.Database.CloseConnection();
            }
        }

        public async Task CheckHasIdentityAsync(DbContext context, CancellationToken cancellationToken) // No longer used
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            var sqlConnection = context.Database.GetDbConnection();
            var currentTransaction = context.Database.CurrentTransaction;
            try
            {
                using (var command = sqlConnection.CreateCommand())
                {
                    if (currentTransaction != null)
                        command.Transaction = currentTransaction.GetDbTransaction();
                    command.CommandText = SqlQueryBuilder.SelectIdentityColumnName(TableName, Schema);
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                IdentityColumnName = reader.GetString(0);
                            }
                        }
                    }
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        public bool CheckTableExist(DbContext context, TableInfo tableInfo)
        {
            bool tableExist = false;
            var sqlConnection = context.Database.GetDbConnection();
            var currentTransaction = context.Database.CurrentTransaction;
            try
            {
                if (currentTransaction == null)
                {
                    if (sqlConnection.State != ConnectionState.Open)
                        sqlConnection.Open();
                }
                using (var command = sqlConnection.CreateCommand())
                {
                    if (currentTransaction != null)
                        command.Transaction = currentTransaction.GetDbTransaction();
                    command.CommandText = SqlQueryBuilder.CheckTableExist(tableInfo.FullTempTableName, tableInfo.BulkConfig.UseTempDB);
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.HasRows)
                        {
                            while (reader.Read())
                            {
                                tableExist = (int)reader[0] == 1;
                            }
                        }
                    }
                }
            }
            finally
            {
                if (currentTransaction == null)
                    sqlConnection.Close();
            }
            return tableExist;
        }

        public async Task<bool> CheckTableExistAsync(DbContext context, TableInfo tableInfo, CancellationToken cancellationToken)
        {
            bool tableExist = false;
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var sqlConnection = context.Database.GetDbConnection();
                var currentTransaction = context.Database.CurrentTransaction;

                using (var command = sqlConnection.CreateCommand())
                {
                    if (currentTransaction != null)
                        command.Transaction = currentTransaction.GetDbTransaction();
                    command.CommandText = SqlQueryBuilder.CheckTableExist(tableInfo.FullTempTableName, tableInfo.BulkConfig.UseTempDB);
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                tableExist = (int)reader[0] == 1;
                            }
                        }
                    }
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            return tableExist;
        }

        protected int GetNumberUpdated(DbContext context)
        {
            var resultParameter = SqlClientHelper.CreateParameter(context.Database.GetDbConnection());
            resultParameter.ParameterName = "@result";
            resultParameter.DbType = DbType.Int32;
            resultParameter.Direction = ParameterDirection.Output;
            string sqlQueryCount = SqlQueryBuilder.SelectCountIsUpdateFromOutputTable(this);
            context.Database.ExecuteSqlRaw($"SET @result = ({sqlQueryCount});", resultParameter);
            return (int)resultParameter.Value;
        }

        protected int GetNumberDeleted(DbContext context)
        {
            var resultParameter = SqlClientHelper.CreateParameter(context.Database.GetDbConnection());
            resultParameter.ParameterName = "@result";
            resultParameter.DbType = DbType.Int32;
            resultParameter.Direction = ParameterDirection.Output;
            string sqlQueryCount = SqlQueryBuilder.SelectCountIsDeleteFromOutputTable(this);
            context.Database.ExecuteSqlRaw($"SET @result = ({sqlQueryCount});", resultParameter);
            return (int)resultParameter.Value;
        }

        protected async Task<int> GetNumberUpdatedAsync(DbContext context, CancellationToken cancellationToken)
        {
            var resultParameters = new List<IDbDataParameter>();
            var p = SqlClientHelper.CreateParameter(context.Database.GetDbConnection());
            p.ParameterName = "@result";
            p.DbType = DbType.Int32;
            p.Direction = ParameterDirection.Output;
            resultParameters.Add(p);
            string sqlQueryCount = SqlQueryBuilder.SelectCountIsUpdateFromOutputTable(this);
            await context.Database.ExecuteSqlRawAsync($"SET @result = ({sqlQueryCount});", resultParameters, cancellationToken).ConfigureAwait(false); // TODO cancellationToken if Not
            return (int)resultParameters.FirstOrDefault().Value;
        }

        protected async Task<int> GetNumberDeletedAsync(DbContext context, CancellationToken cancellationToken)
        {
            var resultParameters = new List<IDbDataParameter>();
            var p = SqlClientHelper.CreateParameter(context.Database.GetDbConnection());
            p.ParameterName = "@result";
            p.DbType = DbType.Int32;
            p.Direction = ParameterDirection.Output;
            resultParameters.Add(p);
            string sqlQueryCount = SqlQueryBuilder.SelectCountIsDeleteFromOutputTable(this);
            await context.Database.ExecuteSqlRawAsync($"SET @result = ({sqlQueryCount});", resultParameters, cancellationToken).ConfigureAwait(false); // TODO cancellationToken if Not
            return (int)resultParameters.FirstOrDefault().Value;
        }

        #endregion

        public static string GetUniquePropertyValues(object entity, List<string> propertiesNames, Dictionary<string, FastProperty> fastPropertyDict)
        {
            StringBuilder uniqueBuilder = new StringBuilder(1024);
            string delimiter = "_"; // TODO: Consider making it Config-urable
            foreach (var propertyName in propertiesNames)
            {
                uniqueBuilder.Append(fastPropertyDict[propertyName].Get(entity).ToString());
                uniqueBuilder.Append(delimiter);
            }
            string result = uniqueBuilder.ToString();
            result = result.Substring(0, result.Length - 1); // removes last delimiter
            return result;
        }

        #region ReadProcedures
        public Dictionary<string, string> ConfigureBulkReadTableInfo(DbContext context)
        {
            InsertToTempTable = true;

            var previousPropertyColumnNamesDict = PropertyColumnNamesDict;
            BulkConfig.PropertiesToInclude = PrimaryKeys.Select(x => x.Key).ToList();
            PropertyColumnNamesDict = PropertyColumnNamesDict.Where(a => PrimaryKeys.ContainsKey(a.Key)).ToDictionary(i => i.Key, i => i.Value);
            return previousPropertyColumnNamesDict;
        }

        public void UpdateReadEntities<T>(IList<T> entities, IList<T> existingEntities)
        {
            UpdateReadEntities<T>(typeof(T), entities, existingEntities);
        }

        public void UpdateReadEntities(Type type, IList<object> entities, IList<object> existingEntities)
        {
            UpdateReadEntities<object>(type, entities, existingEntities);
        }

        internal void UpdateReadEntities<T>(Type type, IList<T> entities, IList<T> existingEntities)
        {
            List<string> propertyNames = PropertyColumnNamesDict.Keys.ToList();
            if (HasOwnedTypes)
            {
                foreach (string ownedTypeName in OwnedTypesDict.Keys)
                {
                    var ownedTypeProperties = OwnedTypesDict[ownedTypeName].ClrType.GetProperties();
                    foreach (var ownedTypeProperty in ownedTypeProperties)
                    {
                        propertyNames.Remove(ownedTypeName + "." + ownedTypeProperty.Name);
                    }
                    propertyNames.Add(ownedTypeName);
                }
            }

            List<string> selectByPropertyNames = PropertyColumnNamesDict.Keys.Where(a => PrimaryKeys.ContainsKey(a)).ToList();

            Dictionary<string, T> existingEntitiesDict = new Dictionary<string, T>();
            foreach (var existingEntity in existingEntities)
            {
                string uniqueProperyValues = GetUniquePropertyValues(existingEntity, selectByPropertyNames, FastPropertyDict);
                existingEntitiesDict.Add(uniqueProperyValues, existingEntity);
            }

            for (int i = 0; i < NumberOfEntities; i++)
            {
                T existingEntity;
                T entity = entities[i];
                string uniqueProperyValues = GetUniquePropertyValues(entity, selectByPropertyNames, FastPropertyDict);
                if (existingEntitiesDict.TryGetValue(uniqueProperyValues, out existingEntity))
                {
                    foreach (var propertyName in propertyNames)
                    {
                        var propertyValue = FastPropertyDict[propertyName].Get(existingEntity);
                        FastPropertyDict[propertyName].Set(entity, propertyValue);
                    }
                }
            }
        }
        #endregion
          
        public void CheckToSetIdentityForPreserveOrder<T>(IList<T> entities, bool reset = false)
        {
            string identityPropertyName = PropertyColumnNamesDict.SingleOrDefault(a => a.Value == IdentityColumnName).Key;



            bool doSetIdentityColumnsForInsertOrder = BulkConfig.PreserveInsertOrder &&
                                                      entities.Count() > 1 &&
                                                      PrimaryKeys.Count() == 1 &&
                                                      PrimaryKeys.Select(x => x.Key).First() == IdentityColumnName &&
                                                      Convert.ToInt64(FastPropertyDict[identityPropertyName].Get(entities[0])) == 0 &&
                                                      Convert.ToInt64(FastPropertyDict[identityPropertyName].Get(entities[1])) == 0;
            if (doSetIdentityColumnsForInsertOrder)
            {
                long i = -entities.Count();
                foreach (var entity in entities)
                {
                    long value = reset ? 0 : i;

                    object idValue;
                    var idType = FastPropertyDict[identityPropertyName].Property.PropertyType;
                    if (idType == typeof(ushort))
                        idValue = (ushort)value;
                    if (idType == typeof(short))
                        idValue = (short)value;
                    else if (idType == typeof(uint))
                        idValue = (uint)value;
                    else if (idType == typeof(int))
                        idValue = (int)value;
                    else if (idType == typeof(ulong))
                        idValue = (ulong)value;
                    else 
                        idValue = (long)value;

                    FastPropertyDict[identityPropertyName].Set(entity, idValue);
                    i++;
                }
            }
        }
      
        protected void UpdateEntitiesIdentity<T>(DbContext context, Type type, IList<T> entities, IList<T> entitiesWithOutputIdentity)
        {
            var identityPropertyName = OutputPropertyColumnNamesDict.SingleOrDefault(a => a.Value == IdentityColumnName).Key;

            if (BulkConfig.PreserveInsertOrder) // Updates PK in entityList
            {
                for (int i = 0; i < NumberOfEntities; i++)
                {
                    var propertyValue = FastPropertyDict[identityPropertyName].Get(entitiesWithOutputIdentity[i]);
                    FastPropertyDict[identityPropertyName].Set(entities[i], propertyValue);

                    if (TimeStampColumnName != null) // timestamp/rowversion is also generated by the SqlServer so if exist should ba updated as well
                    {
                        string timeStampPropertyName = OutputPropertyColumnNamesDict.SingleOrDefault(a => a.Value == TimeStampColumnName).Key;
                        var timeStampPropertyValue = FastPropertyDict[timeStampPropertyName].Get(entitiesWithOutputIdentity[i]);
                        FastPropertyDict[timeStampPropertyName].Set(entities[i], timeStampPropertyValue);
                    }
                }
            }
            else if (BulkConfig.IncludeGraph)
            {
                for (int i = 0; i < NumberOfEntities; i++)
                {
                    if (i == entitiesWithOutputIdentity.Count())
                        break;
                    var originalEntity = entities[i];
                    var outputEntity = entitiesWithOutputIdentity[i];

                    if (context.Entry(originalEntity).IsKeySet == false)
                    {
                        var newPk = context.Entry(outputEntity).Property(identityPropertyName).CurrentValue;
                        context.Entry(originalEntity).Property(identityPropertyName).CurrentValue = newPk;
                    }
                }
            }
            else // Clears entityList and then refills it with loaded entites from Db
            {
                entities.Clear();
                ((List<T>)entities).AddRange(entitiesWithOutputIdentity);
            }
        }

        // Compiled queries created manually to avoid EF Memory leak bug when using EF with dynamic SQL:
        // https://github.com/borisdj/EFCore.BulkExtensions/issues/73
        // Once the following Issue gets fixed(expected in EF 3.0) this can be replaced with code segment: DirectQuery
        // https://github.com/aspnet/EntityFrameworkCore/issues/12905
        #region CompiledQuery
        public void LoadOutputData<T>(DbContext context, IList<T> entities) where T : class
        {
            LoadOutputData<T>(context, typeof(T), entities);
        }

        public void LoadOutputData(DbContext context, Type type, IList<object> entities)
        {
            LoadOutputData<object>(context, type, entities);
        }

        internal void LoadOutputData<T>(DbContext context, Type type, IList<T> entities) where T : class
        {
            bool hasIdentity = OutputPropertyColumnNamesDict.Any(a => a.Value == IdentityColumnName);
            int totallNumber = entities.Count;
            if (BulkConfig.SetOutputIdentity && hasIdentity)
            {
                string sqlQuery = SqlQueryBuilder.SelectFromOutputTable(this);
                var entitiesWithOutputIdentity = (typeof(T) == type) ? QueryOutputTable<T>(context, sqlQuery).ToList() :
                    QueryOutputTable(context, type, sqlQuery).Cast<T>().ToList();

                UpdateEntitiesIdentity(context, type, entities, entitiesWithOutputIdentity);
                totallNumber = entitiesWithOutputIdentity.Count;
            }
            if (BulkConfig.CalculateStats)
            {
                int numberUpdated = GetNumberUpdated(context);
                int numberDeleted = GetNumberDeleted(context);
                BulkConfig.StatsInfo = new StatsInfo
                {
                    StatsNumberUpdated = numberUpdated,
                    StatsNumberDeleted = numberDeleted,
                    StatsNumberInserted = totallNumber - numberUpdated - numberDeleted
                };
            }
        }

        public async Task LoadOutputDataAsync<T>(DbContext context, IList<T> entities, CancellationToken cancellationToken) where T : class
        {
            await LoadOutputDataAsync<T>(context, typeof(T), entities, cancellationToken).ConfigureAwait(false);
        }

        public async Task LoadOutputDataAsync(DbContext context, Type type, IList<object> entities, CancellationToken cancellationToken)
        {
            await LoadOutputDataAsync<object>(context, type, entities, cancellationToken).ConfigureAwait(false);
        }

        internal async Task LoadOutputDataAsync<T>(DbContext context, Type type, IList<T> entities, CancellationToken cancellationToken) where T : class
        {
            bool hasIdentity = OutputPropertyColumnNamesDict.Any(a => a.Value == IdentityColumnName);
            int totallNumber = entities.Count;
            if (BulkConfig.SetOutputIdentity && hasIdentity)
            {
                string sqlQuery = SqlQueryBuilder.SelectFromOutputTable(this);
                //var entitiesWithOutputIdentity = await QueryOutputTableAsync<T>(context, sqlQuery).ToListAsync(cancellationToken).ConfigureAwait(false); // TempFIX
                var entitiesWithOutputIdentity = (typeof(T) == type) ? QueryOutputTable<T>(context, sqlQuery).ToList() :
                    QueryOutputTable(context, type, sqlQuery).Cast<T>().ToList();
                UpdateEntitiesIdentity(context, type, entities, entitiesWithOutputIdentity);
                totallNumber = entitiesWithOutputIdentity.Count;
            }
            if (BulkConfig.CalculateStats)
            {
                int numberUpdated = await GetNumberUpdatedAsync(context, cancellationToken).ConfigureAwait(false);
                int numberDeleted = await GetNumberDeletedAsync(context, cancellationToken).ConfigureAwait(false);
                BulkConfig.StatsInfo = new StatsInfo
                {
                    StatsNumberUpdated = numberUpdated,
                    StatsNumberDeleted = numberDeleted,
                    StatsNumberInserted = totallNumber - numberUpdated - numberDeleted
                };
            }
        }

        protected IEnumerable<T> QueryOutputTable<T>(DbContext context, string sqlQuery) where T : class
        {
            var compiled = EF.CompileQuery(GetQueryExpression<T>(sqlQuery));
            var result = compiled(context);
            return result;
        }

        protected IEnumerable QueryOutputTable(DbContext context, Type type, string sqlQuery)
        {
            var compiled = EF.CompileQuery(GetQueryExpression(type, sqlQuery));
            var result = compiled(context);
            return result;
        }

        /*protected IAsyncEnumerable<T> QueryOutputTableAsync<T>(DbContext context, string sqlQuery) where T : class
        {
            var compiled = EF.CompileAsyncQuery(GetQueryExpression<T>(sqlQuery));
            var result = compiled(context);
            return result;
        }*/

        public Expression<Func<DbContext, IQueryable<T>>> GetQueryExpression<T>(string sqlQuery, bool ordered = true) where T : class
        {
            Expression<Func<DbContext, IQueryable<T>>> expression = null;
            if (BulkConfig.TrackingEntities) // If Else can not be replaced with Ternary operator for Expression
            {
                expression = (ctx) => ctx.Set<T>().FromSqlRaw(sqlQuery);
            }
            else
            {
                expression = (ctx) => ctx.Set<T>().FromSqlRaw(sqlQuery).AsNoTracking();
            }
            return ordered ? Expression.Lambda<Func<DbContext, IQueryable<T>>>(OrderBy(typeof(T), expression.Body, PrimaryKeys.Select(x => x.Key).First()), expression.Parameters) : expression;

            // ALTERNATIVELY OrderBy with DynamicLinq ('using System.Linq.Dynamic.Core;' NuGet required) that eliminates need for custom OrderBy<T> method with Expression.
            //var queryOrdered = query.OrderBy(PrimaryKeys[0]);
        }

        public Expression<Func<DbContext, IEnumerable>> GetQueryExpression(Type entityType, string sqlQuery, bool ordered = true)
        {
            var parameter = Expression.Parameter(typeof(DbContext), "ctx");
            var expression = Expression.Call(parameter, "Set", new Type[] { entityType });
            expression = Expression.Call(typeof(RelationalQueryableExtensions), "FromSqlRaw", new Type[] { entityType }, expression, Expression.Constant(sqlQuery), Expression.Constant(Array.Empty<object>()));
            if (BulkConfig.TrackingEntities) // If Else can not be replaced with Ternary operator for Expression
            {
            }
            else
            {
                expression = Expression.Call(typeof(EntityFrameworkQueryableExtensions), "AsNoTracking", new Type[] { entityType }, expression);
            }
            expression = ordered ? OrderBy(entityType, expression, PrimaryKeys.Select(x => x.Key).First()) : expression;
            return Expression.Lambda<Func<DbContext, IEnumerable>>(expression, parameter);

            // ALTERNATIVELY OrderBy with DynamicLinq ('using System.Linq.Dynamic.Core;' NuGet required) that eliminates need for custom OrderBy<T> method with Expression.
            //var queryOrdered = query.OrderBy(PrimaryKeys[0]);
        }

        private static MethodCallExpression OrderBy(Type entityType, Expression source, string ordering)
        {
            PropertyInfo property = entityType.GetProperty(ordering);
            ParameterExpression parameter = Expression.Parameter(entityType);
            MemberExpression propertyAccess = Expression.MakeMemberAccess(parameter, property);
            LambdaExpression orderByExp = Expression.Lambda(propertyAccess, parameter);
            return Expression.Call(typeof(Queryable), "OrderBy", new Type[] { entityType, property.PropertyType }, source, Expression.Quote(orderByExp));
        }
        #endregion

        // Currently not used until issue from previous segment is fixed in EFCore
        #region DirectQuery
        /*public void UpdateOutputIdentity<T>(DbContext context, IList<T> entities) where T : class
        {
            if (HasSinglePrimaryKey)
            {
                var entitiesWithOutputIdentity = QueryOutputTable<T>(context).ToList();
                UpdateEntitiesIdentity(entities, entitiesWithOutputIdentity);
            }
        }

        public async Task UpdateOutputIdentityAsync<T>(DbContext context, IList<T> entities) where T : class
        {
            if (HasSinglePrimaryKey)
            {
                var entitiesWithOutputIdentity = await QueryOutputTable<T>(context).ToListAsync().ConfigureAwait(false);
                UpdateEntitiesIdentity(entities, entitiesWithOutputIdentity);
            }
        }

        protected IQueryable<T> QueryOutputTable<T>(DbContext context) where T : class
        {
            string q = SqlQueryBuilder.SelectFromOutputTable(this);
            var query = context.Set<T>().FromSql(q);
            if (!BulkConfig.TrackingEntities)
            {
                query = query.AsNoTracking();
            }

            var queryOrdered = OrderBy(query, PrimaryKeys[0]);
            // ALTERNATIVELY OrderBy with DynamicLinq ('using System.Linq.Dynamic.Core;' NuGet required) that eliminates need for custom OrderBy<T> method with Expression.
            //var queryOrdered = query.OrderBy(PrimaryKeys[0]);

            return queryOrdered;
        }

        private static IQueryable<T> OrderBy<T>(IQueryable<T> source, string ordering)
        {
            Type entityType = typeof(T);
            PropertyInfo property = entityType.GetProperty(ordering);
            ParameterExpression parameter = Expression.Parameter(entityType);
            MemberExpression propertyAccess = Expression.MakeMemberAccess(parameter, property);
            LambdaExpression orderByExp = Expression.Lambda(propertyAccess, parameter);
            MethodCallExpression resultExp = Expression.Call(typeof(Queryable), "OrderBy", new Type[] { entityType, property.PropertyType }, source.Expression, Expression.Quote(orderByExp));
            var orderedQuery = source.Provider.CreateQuery<T>(resultExp);
            return orderedQuery;
        }*/
        #endregion
    }
}
