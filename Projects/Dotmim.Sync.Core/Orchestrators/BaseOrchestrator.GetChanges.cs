﻿using Dotmim.Sync.Args;
using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    public abstract partial class BaseOrchestrator
    {

        /// <summary>
        /// Gets a batch of changes to synchronize when given batch size, 
        /// destination knowledge, and change data retriever parameters.
        /// </summary>
        /// <returns>A DbSyncContext object that will be used to retrieve the modified data.</returns>
        internal virtual async Task<(SyncContext, BatchInfo, DatabaseChangesSelected)> InternalGetChangesAsync(
                             SyncContext context, MessageGetChangesBatch message,
                             DbConnection connection, DbTransaction transaction,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            // batch info containing changes
            BatchInfo batchInfo;

            // Statistics about changes that are selected
            DatabaseChangesSelected changesSelected;

            if (context.SyncWay == SyncWay.Upload && context.SyncType == SyncType.Reinitialize)
            {
                (batchInfo, changesSelected) = await this.InternalGetEmptyChangesAsync(message).ConfigureAwait(false);
                return (context, batchInfo, changesSelected);
            }

            // Call interceptor
            var databaseChangesSelectingArgs = new DatabaseChangesSelectingArgs(context, message, connection, transaction);
            await this.InterceptAsync(databaseChangesSelectingArgs, progress, cancellationToken).ConfigureAwait(false);
            
            // create local directory
            if (!string.IsNullOrEmpty(message.BatchDirectory) && !Directory.Exists(message.BatchDirectory))
                Directory.CreateDirectory(message.BatchDirectory);

            changesSelected = new DatabaseChangesSelected();

            // numbers of batch files generated
            var batchIndex = 0;

            // Create a batch 
            // batchinfo generate a schema clone with scope columns if needed
            batchInfo = new BatchInfo(message.Schema, message.BatchDirectory);
            batchInfo.TryRemoveDirectory();
            batchInfo.CreateDirectory();

            var cptSyncTable = 0;
            var currentProgress = context.ProgressPercentage;

            var schemaTables = message.Schema.Tables.SortByDependencies(tab => tab.GetRelations().Select(r => r.GetParentTable()));

            var lstAllBatchPartInfos = new ConcurrentBag<BatchPartInfo>();

            var threadNumberLimits = message.SupportsMultiActiveResultSets ? 8 : 1;

            foreach (var syncTable in schemaTables)
            //await schemaTables.ForEachAsync(async syncTable =>
            {
                if (cancellationToken.IsCancellationRequested)
                    continue;

                var columnsCount = syncTable.GetMutableColumnsWithPrimaryKeys().Count();

                //list of batchpart for that synctable
                var batchPartInfos = new List<BatchPartInfo>();

                // tmp count of table for report progress pct
                cptSyncTable++;

                // Only table schema is replicated, no datas are applied
                if (syncTable.SyncDirection == SyncDirection.None)
                    continue;

                // if we are in upload stage, so check if table is not download only
                if (context.SyncWay == SyncWay.Upload && syncTable.SyncDirection == SyncDirection.DownloadOnly)
                    continue;

                // if we are in download stage, so check if table is not download only
                if (context.SyncWay == SyncWay.Download && syncTable.SyncDirection == SyncDirection.UploadOnly)
                    continue;

                // Get Command
                var selectIncrementalChangesCommand = await this.GetSelectChangesCommandAsync(context, syncTable, message.Setup, message.IsNew, connection, transaction);

                if (selectIncrementalChangesCommand == null) continue;

                var localSerializer = message.LocalSerializerFactory.GetLocalSerializer();

                var schemaChangesTable = DbSyncAdapter.CreateChangesTable(syncTable);

                var (batchPartInfoFullPath, batchPartFileName) = batchInfo.GetNewBatchPartInfoPath(syncTable, batchIndex, localSerializer.Extension);

                // Statistics
                var tableChangesSelected = new TableChangesSelected(syncTable.TableName, syncTable.SchemaName);

                var rowsCountInBatch = 0;

                // Set parameters
                this.SetSelectChangesCommonParameters(context, syncTable, message.ExcludingScopeId, message.IsNew, message.LastTimestamp, selectIncrementalChangesCommand);

                // launch interceptor if any
                var args = await this.InterceptAsync(new TableChangesSelectingArgs(context, syncTable, selectIncrementalChangesCommand, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                if (!args.Cancel && args.Command != null)
                {
                    // open the file and write table header
                    await localSerializer.OpenFileAsync(batchPartInfoFullPath, schemaChangesTable).ConfigureAwait(false);

                    await this.InterceptAsync(new DbCommandArgs(context, args.Command, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                    // Get the reader
                    using var dataReader = await args.Command.ExecuteReaderAsync().ConfigureAwait(false);

                    while (dataReader.Read())
                    {
                        // Create a row from dataReader
                        var syncRow = CreateSyncRowFromReader2(dataReader, schemaChangesTable);
                        rowsCountInBatch++;

                        // Set the correct state to be applied
                        if (syncRow.RowState == DataRowState.Deleted)
                            tableChangesSelected.Deletes++;
                        else if (syncRow.RowState == DataRowState.Modified)
                            tableChangesSelected.Upserts++;

                        await localSerializer.WriteRowToFileAsync(syncRow, schemaChangesTable).ConfigureAwait(false);

                        var currentBatchSize = await localSerializer.GetCurrentFileSizeAsync().ConfigureAwait(false);

                        // Next line if we don't reach the batch size yet.
                        if (currentBatchSize <= message.BatchSize)
                            continue;

                        var bpi = new BatchPartInfo { FileName = batchPartFileName };

                        // Create the info on the batch part
                        BatchPartTableInfo tableInfo = new BatchPartTableInfo
                        {
                            TableName = tableChangesSelected.TableName,
                            SchemaName = tableChangesSelected.SchemaName,
                            RowsCount = rowsCountInBatch

                        };
                        bpi.Tables = new BatchPartTableInfo[] { tableInfo };
                        bpi.RowsCount = rowsCountInBatch;
                        bpi.IsLastBatch = false;
                        bpi.Index = batchIndex;
                        batchPartInfos.Add(bpi);
                        lstAllBatchPartInfos.Add(bpi);

                        // Close file
                        await localSerializer.CloseFileAsync(batchPartInfoFullPath, schemaChangesTable).ConfigureAwait(false);

                        // increment batch index
                        batchIndex++;
                        // Reinit rowscount in batch
                        rowsCountInBatch = 0;

                        // generate a new path
                        (batchPartInfoFullPath, batchPartFileName) = batchInfo.GetNewBatchPartInfoPath(syncTable, batchIndex, localSerializer.Extension);

                        // open a new file and write table header
                        await localSerializer.OpenFileAsync(batchPartInfoFullPath, schemaChangesTable).ConfigureAwait(false);
                    }

                    dataReader.Close();

                }
                // Close file
                await localSerializer.CloseFileAsync(batchPartInfoFullPath, schemaChangesTable).ConfigureAwait(false);

                // Check if we have ..something.
                // Delete folder if nothing
                // Add the BPI to BI if something
                if (rowsCountInBatch == 0 && File.Exists(batchPartInfoFullPath))
                {
                    File.Delete(batchPartInfoFullPath);
                }
                else
                {
                    var bpi2 = new BatchPartInfo { FileName = batchPartFileName };

                    // Create the info on the batch part
                    BatchPartTableInfo tableInfo2 = new BatchPartTableInfo
                    {
                        TableName = tableChangesSelected.TableName,
                        SchemaName = tableChangesSelected.SchemaName,
                        RowsCount = rowsCountInBatch
                    };
                    bpi2.Tables = new BatchPartTableInfo[] { tableInfo2 };
                    bpi2.RowsCount = rowsCountInBatch;
                    bpi2.IsLastBatch = true;
                    bpi2.Index = batchIndex;
                    lstAllBatchPartInfos.Add(bpi2);
                    batchPartInfos.Add(bpi2);
                    batchIndex++;

                }

                // We don't report progress if no table changes is empty, to limit verbosity
                if (tableChangesSelected.Deletes > 0 || tableChangesSelected.Upserts > 0)
                    changesSelected.TableChangesSelected.Add(tableChangesSelected);

                // even if no rows raise the interceptor
                var tableChangesSelectedArgs = new TableChangesSelectedArgs(context, batchPartInfos, tableChangesSelected, connection, transaction);
                await this.InterceptAsync(tableChangesSelectedArgs, progress, cancellationToken).ConfigureAwait(false);

                context.ProgressPercentage = currentProgress + (cptSyncTable * 0.2d / message.Schema.Tables.Count);
            }
            //}, threadNumberLimits);

            // delete all empty batchparts (empty tables)
            foreach (var bpi in lstAllBatchPartInfos.Where(bpi => bpi.RowsCount <= 0))
                File.Delete(Path.Combine(batchInfo.GetDirectoryFullPath(), bpi.FileName));

            // Generate a good index order to be compliant with previous versions
            var tmpLstBatchPartInfos = new List<BatchPartInfo>();
            foreach (var table in schemaTables)
            {
                // get all bpi where count > 0 and ordered by index
                foreach (var bpi in lstAllBatchPartInfos.Where(bpi => bpi.RowsCount > 0 && bpi.Tables[0].EqualsByName(new BatchPartTableInfo(table.TableName, table.SchemaName))).OrderBy(bpi => bpi.Index).ToArray())
                {
                    batchInfo.BatchPartsInfo.Add(bpi);
                    batchInfo.RowsCount += bpi.RowsCount;

                    tmpLstBatchPartInfos.Add(bpi);
                }
            }

            var newBatchIndex = 0;
            foreach (var bpi in tmpLstBatchPartInfos)
            {
                bpi.Index = newBatchIndex;
                newBatchIndex++;
                bpi.IsLastBatch = newBatchIndex == tmpLstBatchPartInfos.Count;
            }

            //Set the total rows count contained in the batch info
            batchInfo.EnsureLastBatch();


            if (batchInfo.RowsCount <= 0)
                batchInfo.Clear(true);

            var databaseChangesSelectedArgs = new DatabaseChangesSelectedArgs(context, message.LastTimestamp, batchInfo, changesSelected, connection);
            await this.InterceptAsync(databaseChangesSelectedArgs, progress, cancellationToken).ConfigureAwait(false);

            return (context, batchInfo, changesSelected);

        }


        /// <summary>
        /// Gets changes rows count estimation, 
        /// </summary>
        internal virtual async Task<(SyncContext, DatabaseChangesSelected)> InternalGetEstimatedChangesCountAsync(
                             SyncContext context, MessageGetChangesBatch message,
                             DbConnection connection, DbTransaction transaction,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {
            // Call interceptor
            await this.InterceptAsync(new DatabaseChangesSelectingArgs(context, message, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

            // Create stats object to store changes count
            var changes = new DatabaseChangesSelected();

            if (context.SyncWay == SyncWay.Upload && context.SyncType == SyncType.Reinitialize)
                return (context, changes);

            var threadNumberLimits = message.SupportsMultiActiveResultSets ? 8 : 1;

            await message.Schema.Tables.ForEachAsync(async syncTable =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                // Only table schema is replicated, no datas are applied
                if (syncTable.SyncDirection == SyncDirection.None)
                    return;

                // if we are in upload stage, so check if table is not download only
                if (context.SyncWay == SyncWay.Upload && syncTable.SyncDirection == SyncDirection.DownloadOnly)
                    return;

                // if we are in download stage, so check if table is not download only
                if (context.SyncWay == SyncWay.Download && syncTable.SyncDirection == SyncDirection.UploadOnly)
                    return;

                // Get Command
                var command = await this.GetSelectChangesCommandAsync(context, syncTable, message.Setup, message.IsNew, connection, transaction);

                if (command == null) return;

                // Set parameters
                this.SetSelectChangesCommonParameters(context, syncTable, message.ExcludingScopeId, message.IsNew, message.LastTimestamp, command);

                // launch interceptor if any
                var args = new TableChangesSelectingArgs(context, syncTable, command, connection, transaction);
                await this.InterceptAsync(args, progress, cancellationToken).ConfigureAwait(false);

                if (args.Cancel || args.Command == null)
                    return;

                // Statistics
                var tableChangesSelected = new TableChangesSelected(syncTable.TableName, syncTable.SchemaName);

                await this.InterceptAsync(new DbCommandArgs(context, args.Command, connection, transaction), progress, cancellationToken).ConfigureAwait(false);
                // Get the reader
                using var dataReader = await args.Command.ExecuteReaderAsync().ConfigureAwait(false);

                while (dataReader.Read())
                {
                    bool isTombstone = false;
                    for (var i = 0; i < dataReader.FieldCount; i++)
                    {
                        if (dataReader.GetName(i) == "sync_row_is_tombstone")
                        {
                            isTombstone = Convert.ToInt64(dataReader.GetValue(i)) > 0;
                            break;
                        }
                    }

                    // Set the correct state to be applied
                    if (isTombstone)
                        tableChangesSelected.Deletes++;
                    else
                        tableChangesSelected.Upserts++;
                }

                dataReader.Close();

                // Check interceptor
                var changesArgs = new TableChangesSelectedArgs(context, null, tableChangesSelected, connection, transaction);
                await this.InterceptAsync(changesArgs, progress, cancellationToken).ConfigureAwait(false);

                if (tableChangesSelected.Deletes > 0 || tableChangesSelected.Upserts > 0)
                    changes.TableChangesSelected.Add(tableChangesSelected);

            }, threadNumberLimits);

            // Raise database changes selected
            var databaseChangesSelectedArgs = new DatabaseChangesSelectedArgs(context, message.LastTimestamp, null, changes, connection);
            await this.InterceptAsync(databaseChangesSelectedArgs, progress, cancellationToken).ConfigureAwait(false);

            return (context, changes);
        }

        /// <summary>
        /// Generate an empty BatchInfo
        /// </summary>
        internal Task<(BatchInfo, DatabaseChangesSelected)> InternalGetEmptyChangesAsync(MessageGetChangesBatch message)
        {
            // Create the batch info, in memory
            var batchInfo = new BatchInfo(message.Schema, message.BatchDirectory); ;

            // Create a new empty in-memory batch info
            return Task.FromResult((batchInfo, new DatabaseChangesSelected()));

        }


        /// <summary>
        /// Get the correct Select changes command 
        /// Can be either
        /// - SelectInitializedChanges              : All changes for first sync
        /// - SelectChanges                         : All changes filtered by timestamp
        /// - SelectInitializedChangesWithFilters   : All changes for first sync with filters
        /// - SelectChangesWithFilters              : All changes filtered by timestamp with filters
        /// </summary>
        internal async Task<DbCommand> GetSelectChangesCommandAsync(SyncContext context, SyncTable syncTable, SyncSetup setup, bool isNew, DbConnection connection, DbTransaction transaction)
        {
            DbCommandType dbCommandType;

            SyncFilter tableFilter = null;

            var syncAdapter = this.GetSyncAdapter(syncTable, setup);

            // Check if we have parameters specified

            // Sqlite does not have any filter, since he can't be server side
            if (this.Provider.CanBeServerProvider)
                tableFilter = syncTable.GetFilter();

            var hasFilters = tableFilter != null;

            // Determing the correct DbCommandType
            if (isNew && hasFilters)
                dbCommandType = DbCommandType.SelectInitializedChangesWithFilters;
            else if (isNew && !hasFilters)
                dbCommandType = DbCommandType.SelectInitializedChanges;
            else if (!isNew && hasFilters)
                dbCommandType = DbCommandType.SelectChangesWithFilters;
            else
                dbCommandType = DbCommandType.SelectChanges;

            // Get correct Select incremental changes command 
            var (command, _) = await syncAdapter.GetCommandAsync(dbCommandType, connection, transaction, tableFilter);

            return command;
        }

        /// <summary>
        /// Set common parameters to SelectChanges Sql command
        /// </summary>
        internal void SetSelectChangesCommonParameters(SyncContext context, SyncTable syncTable, Guid? excludingScopeId, bool isNew, long? lastTimestamp, DbCommand selectIncrementalChangesCommand)
        {
            // Set the parameters
            DbSyncAdapter.SetParameterValue(selectIncrementalChangesCommand, "sync_min_timestamp", lastTimestamp);
            DbSyncAdapter.SetParameterValue(selectIncrementalChangesCommand, "sync_scope_id", excludingScopeId.HasValue ? (object)excludingScopeId.Value : DBNull.Value);

            // Check filters
            SyncFilter tableFilter = null;

            // Sqlite does not have any filter, since he can't be server side
            if (this.Provider.CanBeServerProvider)
                tableFilter = syncTable.GetFilter();

            var hasFilters = tableFilter != null;

            if (!hasFilters)
                return;

            // context parameters can be null at some point.
            var contexParameters = context.Parameters ?? new SyncParameters();

            foreach (var filterParam in tableFilter.Parameters)
            {
                var parameter = contexParameters.FirstOrDefault(p =>
                    p.Name.Equals(filterParam.Name, SyncGlobalization.DataSourceStringComparison));

                object val = parameter?.Value;

                DbSyncAdapter.SetParameterValue(selectIncrementalChangesCommand, filterParam.Name, val);
            }

        }

        /// <summary>
        /// Create a new SyncRow from a dataReader.
        /// </summary>
        internal SyncRow CreateSyncRowFromReader2(IDataReader dataReader, SyncTable schemaTable)
        {
            // Create a new row, based on table structure

            var syncRow = new SyncRow(schemaTable);

            bool isTombstone = false;

            for (var i = 0; i < dataReader.FieldCount; i++)
            {
                var columnName = dataReader.GetName(i);

                // if we have the tombstone value, do not add it to the table
                if (columnName == "sync_row_is_tombstone")
                {
                    isTombstone = Convert.ToInt64(dataReader.GetValue(i)) > 0;
                    continue;
                }
                if (columnName == "sync_update_scope_id")
                    continue;

                var columnValueObject = dataReader.GetValue(i);
                var columnValue = columnValueObject == DBNull.Value ? null : columnValueObject;

                syncRow[i] = columnValue;
            }

            syncRow.RowState = isTombstone ? DataRowState.Deleted : DataRowState.Modified;
            return syncRow;
        }



    }
}
