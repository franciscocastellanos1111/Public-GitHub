using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Configuration;
using System.Diagnostics;
using System.Data.SqlClient;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System.Xml.Linq;
using Microsoft.Identity.Client;
using System.Runtime.Remoting.Services;
using Newtonsoft.Json.Linq;
using System.Net.NetworkInformation;
using Azure.Data.Tables;
using Azure;

namespace DynamicsProcesses
{
    internal class ParallelProcessesHelper
    {
        public static TableClient SemaphoreClient;
        public static int MaxConcurrentProcesses = 1;


        public static TableClient getTableClientAsync(string connectionString)
        {
            var tableClient = new TableClient(connectionString, "semaphores");
            // Ensure table exists
            try
            {
                tableClient.CreateIfNotExists();
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog("Error creating semaphores table: " + ex.Message);
            }
            return tableClient;
        }





        public static async Task<bool> tryAcquireSemaphoreAsync(string resourceId, int maxConcurrent)
        {
            SemaphoreEntity semaphoreEntity = new SemaphoreEntity
            {
                PartitionKey = "semaphore",
                RowKey = resourceId,
                CurrentCount = 0,
                MaxConcurrent = maxConcurrent,
                LastUpdated = DateTime.UtcNow
            };

            #region Try To Acquire Semaphore
            try
            {
                // Try to get existing semaphore
                var existingEntity = await SemaphoreClient.GetEntityAsync<SemaphoreEntity>("semaphore", resourceId);
                semaphoreEntity = existingEntity.Value;

                // Check if we can acquire
                if (semaphoreEntity.CurrentCount >= maxConcurrent)
                {
                    DynamicsInterface.writeToLog("resourceId: " + resourceId + " - semaphore limit reached: " + semaphoreEntity.CurrentCount + "/" + maxConcurrent);
                    return false;
                }

                // Increment count with optimistic concurrency
                semaphoreEntity.CurrentCount++;
                semaphoreEntity.LastUpdated = DateTime.UtcNow;

                await SemaphoreClient.UpdateEntityAsync(semaphoreEntity, semaphoreEntity.ETag, TableUpdateMode.Replace);
                //DynamicsInterface.writeToLog("resourceId: " + resourceId + " successfully acquired - count now: " + semaphoreEntity.CurrentCount);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Semaphore doesn't exist, create it with count = 1
                semaphoreEntity.CurrentCount = 1;
                try
                {
                    await SemaphoreClient.AddEntityAsync(semaphoreEntity);
                    DynamicsInterface.writeToLog("resourceId: " + resourceId + " created new semaphore and acquired - count: 1");
                    return true;
                }
                catch (RequestFailedException createEx) when (createEx.Status == 409)
                {
                    // Someone else created it simultaneously, retry
                    DynamicsInterface.writeToLog("resourceId: " + resourceId + " - concurrent creation detected, retrying");
                    await Task.Delay(50); // Small delay to avoid tight retry loop
                    return await tryAcquireSemaphoreAsync(resourceId, maxConcurrent);
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Concurrency conflict during update, retry
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - concurrency conflict, retrying");
                await Task.Delay(new Random().Next(10, 100)); // Jitter to reduce collision probability
                return await tryAcquireSemaphoreAsync(resourceId, maxConcurrent);
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - unexpected error acquiring semaphore: " + ex.Message);
                return false;
            }
            #endregion

            return false; // Should never reach here, but safety fallback
        }






        public static async Task releaseSemaphoreAsync(string resourceId)
        {
            try
            {
                var entity = await SemaphoreClient.GetEntityAsync<SemaphoreEntity>("semaphore", resourceId);
                SemaphoreEntity semaphoreEntity = entity.Value;

                // Decrement count but don't go below 0
                if (semaphoreEntity.CurrentCount > 0)
                {
                    semaphoreEntity.CurrentCount--;
                    semaphoreEntity.LastUpdated = DateTime.UtcNow;

                    // Actually update the entity in the table
                    await SemaphoreClient.UpdateEntityAsync(semaphoreEntity, semaphoreEntity.ETag, TableUpdateMode.Replace);
                    //DynamicsInterface.writeToLog("resourceId: " + resourceId + " successfully released - count now: " + semaphoreEntity.CurrentCount);
                }
                else
                {
                    DynamicsInterface.writeToLog("resourceId: " + resourceId + " - attempted to release but count was already 0");
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - semaphore not found during release");
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Concurrency conflict during release, retry
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - concurrency conflict during release, retrying");
                await Task.Delay(new Random().Next(10, 100));
                await releaseSemaphoreAsync(resourceId);
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - error releasing semaphore: " + ex.Message);
            }
        }


        public static async Task<SemaphoreEntity> getSemaphoreStatusAsync(string resourceId)
        {
            try
            {
                var entity = await SemaphoreClient.GetEntityAsync<SemaphoreEntity>("semaphore", resourceId);
                return entity.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - semaphore not found");
                return null;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                DynamicsInterface.writeToLog("Error in getSemaphoreStatusAsync(...). Exception message: " + Environment.NewLine + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog("resourceId: " + resourceId + " - error getting semaphore status: " + ex.Message);
                return null;
            }
        }


        public static async Task<int> cleanUpExpiredSemaphores(int expiryHours = 2)
        {
            int deletedCount = 0;
            try
            {
                DynamicsInterface.writeToLog("Cleaning up semaphores older than " + expiryHours + " hours");

                List<SemaphoreEntity> entities = SemaphoreClient.Query<SemaphoreEntity>(e => e.PartitionKey == "semaphore" && e.LastUpdated < DateTime.UtcNow.AddHours(-expiryHours)).ToList();

                DynamicsInterface.writeToLog("Found " + entities.Count + " expired semaphores to clean up");

                foreach (SemaphoreEntity entity in entities)
                {
                    try
                    {
                        await SemaphoreClient.DeleteEntityAsync("semaphore", entity.RowKey);
                        deletedCount++;
                        DynamicsInterface.writeToLog("Deleted expired semaphore: " + entity.RowKey);
                    }
                    catch (RequestFailedException e) when (e.Status == 404)
                    {
                        // Already deleted, continue
                        continue;
                    }
                    catch (Exception ex)
                    {
                        DynamicsInterface.writeToLog("Error deleting semaphore " + entity.RowKey + ": " + ex.Message);
                    }
                }
            }
            catch (Exception e)
            {
                DynamicsInterface.writeToLog("Error in cleanUpExpiredSemaphores(...). Exception message: " + Environment.NewLine + e.Message);
            }

            DynamicsInterface.writeToLog("Completed semaphores cleanup - deleted " + deletedCount + " items");
            return deletedCount;
        }

        // Utility method for testing and debugging
        public static async Task<List<SemaphoreEntity>> getAllSemaphoresAsync()
        {
            try
            {
                List<SemaphoreEntity> entities = SemaphoreClient.Query<SemaphoreEntity>(e => e.PartitionKey == "semaphore").ToList();
                DynamicsInterface.writeToLog("Found " + entities.Count + " active semaphores");

                foreach (var entity in entities)
                {
                    DynamicsInterface.writeToLog("Semaphore: " + entity.RowKey + " - Count: " + entity.CurrentCount + "/" + entity.MaxConcurrent + " - LastUpdated: " + entity.LastUpdated);
                }

                return entities;
            }
            catch (Exception ex)
            {
                DynamicsInterface.writeToLog("Error getting all semaphores: " + ex.Message);
                return new List<SemaphoreEntity>();
            }
        }

    }


    public class SemaphoreEntity : ITableEntity
    {
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public int CurrentCount { get; set; }
        public int MaxConcurrent { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class SemaphoreRequest
    {
        public string ResourceId { get; set; }
        public int MaxConcurrent { get; set; }
    }
}







