using System;
using System.Collections;
using System.Data;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json.Linq;

namespace YelpDataETL.Loaders
{
    public class BusinessLoader
    {
        private static string InsertBusinessSql =>
            @"INSERT INTO business (
                business_id,
                name,
                full_address,
                city,
                state,
                latitude,
                longitude,
                stars,
                review_count,
                open)
            VALUES (
                @business_id,
                @name,
                @full_address,
                @city,
                @state,
                @lat,
                @long,
                @stars,
                @review_count,
                @open);";

        private static string InsertCategorySqlFormat =>
            @"INSERT INTO business_category_{0} (
                business_id,
                {1})
            VALUES (
                @business_id,
                {2});";

        private static string InsertAttributeSqlFormat =>
            @"INSERT INTO business_attribute_{0} (
                business_id,
                {1})
            VALUES (
                @business_id,
                {2});";

        private static string InsertHoursSql =>
            @"INSERT INTO business_hour (
                business_id,
                day,
                close,
                open)
            VALUES (
                @business_id,
                @day,
                @close,
                @open);";

        public static void Load(IDbConnection connection)
        {
            Console.WriteLine($"{nameof(BusinessLoader)} - Starting load...");

            var records = ParseBusinesJson();
            var businessList = records as IList<Business> ?? records.ToList();

            CreateCategoryAndArributeTables(businessList);    //disposing connection here until code is refactored
            InsertBusinessData(businessList);

            Console.WriteLine($"{nameof(BusinessLoader)} - Load complete.");
        }

        private static IEnumerable<Business> ParseBusinesJson()
        {
            var records = File.ReadLines(Helpers.GetFullFilename("yelp_academic_dataset_business"))
                .Select(JObject.Parse)
                .Select(x => {
                    //Build deserialized object
                    var record = new Business {
                        BusinessId = (string)x["business_id"],
                        Name = (string)x["name"],
                        FullAddress = (string)x["full_address"],
                        City = (string)x["city"],
                        State = (string)x["state"],
                        Latitude = x["latitude"] == null ? default(float) : (float)x["latitude"],
                        Longitude = x["longitude"] == null ? default(float) : (float)x["longitude"],
                        Stars = x["stars"] == null ? default(float) : (float)x["stars"],
                        ReviewCount = x["review_count"] == null ? 0 : (int)x["review_count"],
                        Open = (bool)x["open"]
                    };

                    foreach (var category in x["categories"].Children())
                        record.Categories.Add(category.ToString());

                    foreach (var attribute in x["attributes"].Children())
                    {
                        var property = ((JProperty)attribute);
                        var propertyType = GetClrType(property.Value.Type);

                        if (propertyType == typeof(IEnumerable)) { }
                        else
                        {
                            record.Attributes.Add(new BusinessAttributeInfo {
                                Path = attribute.Path,
                                Key = property.Name.ToLower(),
                                Value = property.Value.ToString(),
                                ValueType = propertyType
                            });
                        }

                    }

                    foreach (var hour in x["hours"])
                    {
                        var property = ((JProperty)hour);
                        string day = property.Name.ToUpper();
                        string close = property.Value["close"].ToString();
                        string open = property.Value["open"].ToString();

                        record.Hours.Add(new BusinessHour {
                            Day = day,
                            Open = open,
                            Close = close
                        });
                    }

                    return record;
                });
            return records;
        }

        private static void InsertBusinessData(IEnumerable<Business> businesses)
        {
            //Breaking conventions beceause I need them connections in my life!
            var conn = Helpers.CreateConnectionToYelpDb();

            conn.Open();

            var tran = conn.BeginTransaction();

            var businessList = businesses as IList<Business> ?? businesses.ToList();

            try
            {
                Console.WriteLine($"{nameof(BusinessLoader)} - Inserting categories...");

                InsertCategories(conn, tran, businessList);
                tran.Commit();

                Console.WriteLine($"{nameof(BusinessLoader)} - Categories inserted.");
            }
            catch (Exception)
            {
                Console.WriteLine($"{nameof(BusinessLoader)} - Rolling back category insert...");
                tran.Rollback();
                Console.WriteLine($"{nameof(BusinessLoader)} - Category insert rollback complete.");
            }
            finally
            {
                tran.Dispose();
                conn.Close();
                conn.Dispose();
            }
   
            conn = Helpers.CreateConnectionToYelpDb();

            conn.Open();

            tran = conn.BeginTransaction();

            try
            {
                Console.WriteLine($"{nameof(BusinessLoader)} - Inserting businesses and their hours...");

                foreach (var business in businessList)
                {
                    foreach (var hour in business.Hours)
                    {
                        conn.Execute(InsertHoursSql, new {
                            @business_id = business.BusinessId,
                            @day = hour.Day,
                            @open = hour.Open,
                            @close = hour.Close
                        }, tran);
                    }

                    conn.Execute(InsertBusinessSql, new {
                        @business_id = business.BusinessId,
                        @name = business.Name,
                        @full_address = business.FullAddress,
                        @city = business.City,
                        @state = business.State,
                        @lat = business.Latitude,
                        @long = business.Longitude,
                        @stars = business.Stars,
                        @review_count = business.ReviewCount,
                        @open = business.Open
                    }, tran);
                }

                tran.Commit();

                Console.WriteLine($"{nameof(BusinessLoader)} - Businesses and their hours inserted.");
            }
            catch (Exception)
            {
                tran.Rollback();
                throw;
            }
            finally
            {
                tran.Dispose();
                conn.Close();
                conn.Dispose();
            }
        }

        private static void InsertCategories(IDbConnection connection, IDbTransaction transaction, IList<Business> businesses)
        {
            foreach (var record in businesses)
            {
                //Insert categories
                foreach (string insertScript in BuildCategoryInsertSql(record.Categories))
                {
                    connection.Execute(insertScript, new {
                        business_id = record.BusinessId
                    }, transaction);
                }
            }
        }

        private static void CreateCategoryAndArributeTables(IList<Business> businesses)
        {
            var attributes = businesses
                .SelectMany(x => x.Attributes)
                .DistinctBy(y => y.Key);

            var categories = businesses
                .SelectMany(x => x.Categories)
                .Distinct();

            var sqlScripts = BuildAtrributeTables(attributes).Union(BuildCategoryTables(categories));
            var connection = Helpers.CreateConnectionToYelpDb();

            connection.Open();
            var transaction = connection.BeginTransaction();

            try
            {
                Console.WriteLine($"{nameof(BusinessLoader)} - Creating attribute and category tables...");

                foreach (string script in sqlScripts) connection.Execute(script, transaction);
                transaction.Commit();

                Console.WriteLine($"{nameof(BusinessLoader)} - Tables created.");

            }
            catch (Exception ex)
            {
                Console.WriteLine($"{nameof(BusinessLoader)} - ERROR: {ex.Message}. Rolling back");
                transaction.Rollback();
                Console.WriteLine($"{nameof(BusinessLoader)} - Rollback completed.");

                throw;
            }
            finally
            {
                transaction.Dispose();
                connection.Close();
                connection.Dispose();
            }
        }

        private static IEnumerable<string> BuildCategoryTables(IEnumerable<string> categories)
        {
            var partitionedCategories = Partition(categories);
            var categoryTables = new List<string>();

            for (var i = 0; i < partitionedCategories.Count; i++)
            {
                string columns = string.Join(", ",
                    partitionedCategories[i].Select(x => DatabaseStringify(x) + $" SMALLINT NULL DEFAULT 0"));

                categoryTables.Add(
                    $"DROP TABLE IF EXISTS business_category_{i + 1}; CREATE TABLE business_category_{i + 1} ( business_id NVARCHAR(45) NOT NULL, {columns}, PRIMARY KEY(business_id));");
            }

            return categoryTables;
        }

        private static IEnumerable<string> BuildAtrributeTables(IEnumerable<BusinessAttributeInfo> attributes)
        {
            var businessAttributeInfos = attributes as IList<BusinessAttributeInfo> ?? attributes.ToList();

            var objs = businessAttributeInfos.Where(attr => attr.ValueType == typeof(object)).ToList();
            var groupedAttributes = businessAttributeInfos.GroupBy(x => x.Key);
            //var partitionedAttributes = Partition(attributes);
            var attributeTables = new List<string>();

            return attributeTables;
        }

        private static IEnumerable<string> BuildCategoryInsertSql(IEnumerable<string> categories)
        {
            var partitionedCategories = Partition(categories);
            var categoryInserts = new List<string>();

            for (var i = 0; i < partitionedCategories.Count; i++)
            {
                if (partitionedCategories[i].Count <= 0) continue;

                string colsToInsert = string.Join(", ", partitionedCategories[i].Select(DatabaseStringify));
                string valsToInsert = string.Join(", ", Enumerable.Repeat(true, partitionedCategories[i].Count));
                string sql = string.Format(InsertCategorySqlFormat, i + 1, colsToInsert, valsToInsert);

                categoryInserts.Add(sql);
            }

            return categoryInserts;
        }

        private static Dictionary<int, List<string>> Partition(IEnumerable<string> xs, int numberOfPartitions = 10)
        {
            var partitions = new Dictionary<int, List<string>>();

            for (var i = 0; i < numberOfPartitions; i++)
            {
                partitions.Add(i, new List<string>());
            }

            foreach (string x in xs)
            {
                ulong idx = Fnv1Hash.Create(Encoding.ASCII.GetBytes(x)) % (ulong)partitions.Count;

                partitions[(int)idx].Add(x);
            }

            return partitions;
        }

        private static Type GetClrType(JTokenType jType)
        {
            switch (jType)
            {
                case JTokenType.Boolean:
                    return typeof(bool);
                case JTokenType.String:
                    return typeof(string);
                case JTokenType.Integer:
                    return typeof(int);
                default:
                    return typeof(IEnumerable);
            }
        }

        private static string DatabaseStringify(string str)
        {
            string result = str
                .Trim()
                .ToLower()
                .Replace("(", " ")
                .Replace("-", " ")
                .Replace(")", "")
                .Replace("'", "")
                .Replace(",", "")
                .Replace("/", " ")
                .Replace("&", "and")
                .Replace(" ", "_");

            return result;
        }
    }
}