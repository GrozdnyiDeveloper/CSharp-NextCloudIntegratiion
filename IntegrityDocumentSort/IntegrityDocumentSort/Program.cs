using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.PeerToPeer;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Xrm.Sdk.Query;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Shapes;
using System.Web.Services.Description;

namespace IntegrityDocumentSort
{
    internal class Program
    {
        static CrmServiceClient crmServiceClient;
        static StorageLocationManager locationManager;
        static WebDavManager web;
        static string baseUrl;
        static int totalCount = 0, unfoundCount = 0, transferCount = 0, failedCount = 0, matchedCount = 0;
        static StreamWriter logUnfound, logSession, logCopy;
        static void Main(string[] args)
        {
            baseUrl = System.Configuration.ConfigurationManager.ConnectionStrings["NextCloud"].ConnectionString;
            crmServiceClient = new CrmServiceClient(System.Configuration.ConfigurationManager.ConnectionStrings["CRM"].ConnectionString);
            web = new WebDavManager(crmServiceClient);
            locationManager = new StorageLocationManager(crmServiceClient, baseUrl, web);

            if (crmServiceClient.IsReady)
            {
                // Создаём файлы логов
                Console.WriteLine("Генерируем файлы логов");
                var logDirPath = System.Configuration.ConfigurationManager.AppSettings["Log"] + "\\log_" + DateTime.Now.ToString().Replace(':', '-');
                DirectoryInfo dirInfo = new DirectoryInfo(logDirPath);
                dirInfo.Create();
                logUnfound = new StreamWriter(logDirPath + "\\UndiscoveredLog.txt", true) { AutoFlush = true };
                logSession = new StreamWriter(logDirPath + "\\SessionLog.txt", true) { AutoFlush = true };
                logCopy = new StreamWriter(logDirPath + "\\CopyLog.txt", true) { AutoFlush = true };
                Console.WriteLine($"Файлы успешно сгенерированны по пути: {logDirPath}");

                // Получения пути
                var path = Uri.UnescapeDataString(System.Configuration.ConfigurationManager.AppSettings["Path"]);
                // Если путь получен
                if (path != null && path != "")
                {
                    WriteLog($"Ищем компоненты NextCloud по пути {path}", new List<StreamWriter> { logSession });
                    /*
                        Получение всех папок/подпапок и файлов по всей иерархии
                        Получение происходит за 1 запрос с Depth = "infinity"
                        Быстрее, чем рекурсивное получение, но при обработке
                        больших данных (для Проекта) может вызвать timeout на сервере
                    */
                    var folderContent = SingleRequestContent($"{baseUrl}/{path}");
                    /*
                        Получение всех папок/подпапок и файлов по всей иерархии
                        рекурсивно за множество запросов
                        Медленнее чем за 1 запрос, но стабильнее
                    */
                    // var folderContent = MultipleRequestContent($"{baseUrl}/{path}"); 
                    // Если получены данные из NextCloud
                    if (folderContent.Count > 0)
                    {
                        WriteLog("Компоненты успешно найдены. Начинаем сканирование. ", new List<StreamWriter> { logSession });
                        // Вызов метода сканирования данных
                        var mainItem = new NextCloudItem()
                        {
                            Name = System.IO.Path.GetFileName(path),
                            IsFolder = true,
                            Content = folderContent,
                            FullPath = $"{baseUrl}/{path}"
                        };
                        // Запуск сканирования файлов
                        ScanFiles(mainItem);
                        WriteLog($"Программа завершила свою работу. \n" +
                            $"Всего файлов: {totalCount} (найдено в CRM: {totalCount - unfoundCount}, не найдено: {unfoundCount}); Совпало: {matchedCount}; Скопировано: {transferCount + failedCount} (успешно: {transferCount}, неудачно: {failedCount}). ", 
                            new List<StreamWriter> { logSession });
                        Console.ReadLine();
                    } 
                    else
                    {
                        WriteLog("Папка по заданному не найдена или она пуста. ", new List<StreamWriter> { logSession });
                        Console.ReadLine();
                    }
                }
                else
                {
                    WriteLog("В файле конфигурации необходимо ввести корректный путь в NextCloud. ", new List<StreamWriter> { logSession });
                    Console.ReadLine();
                }
                //Закрываем потоки для записи логов
                logSession.Close();
                logCopy.Close();
                logUnfound.Close();
            }
            else
            {
                Console.WriteLine("Не удалось подключиться к CRM. ");
                Console.ReadLine();
            }
        }

        static void WriteLog(string message, List<StreamWriter> logFiles = default(List<StreamWriter>))
        {
            Console.WriteLine($"{message}");
            foreach (StreamWriter logFile in logFiles)
            {
                logFile.WriteLine($"{message}");
            }
        }

        // Получение данных из NextCloud за 1 запрос
        static List<NextCloudItem> SingleRequestContent(string path)
        {
            // Получение всех папок/подпапок и файлов по всей иерархии за 1 запрос с Depth = "infinity"
            var folderContent = web.GetFolderContent(path, "infinity");
            // Сортировка полученных данных в иерархию
            var sortedContent = SortContent(folderContent, path);
            return sortedContent;
        }

        // Метод сортировки данных в иерархию
        static List<NextCloudItem> SortContent(List<NextCloudItem> content, string currentPath)
        {
            var result = new List<NextCloudItem>();

            foreach (var item in content)
            {
                // Получаем путь до родительской папки
                var formatedPath = item.FullPath.Remove(item.FullPath.LastIndexOf('/'), item.FullPath.Length - item.FullPath.LastIndexOf('/'));
                // Если путь до родительской папки равен текущему пути метода
                if (formatedPath == currentPath)
                {
                    // Если объект - папка
                    if (item.IsFolder)
                    {
                        // Рекурсивно запускаем метод для получение её содержимого
                        item.Content = SortContent(content, $"{currentPath}/{item.Name}");
                    }
                    // Добавляем объект как содержимое родительской папки
                    result.Add(item);
                }
            }
            return result;
        }

        // Рекурсивное получение данных за множество запросов
        static List<NextCloudItem> MultipleRequestContent(string folderPath)
        {
            // Получение содержимого папки по заданному пути
            List<NextCloudItem> items = web.GetFolderContent(folderPath, "1");

            // Производим анализ файлов в текущей папке
            foreach (var item in items)
            {
                // Если объект - папка
                if (item.IsFolder)
                {
                    // Рекурсивно вызываем мметод для получения содержимого папки
                    item.Content = MultipleRequestContent(item.FullPath);
                }
            }

            // Возвращаем список найденных файлов
            return items;
        }

        // Метод сканирования файлов
        static void ScanFiles(NextCloudItem mainItem)
        {
            foreach (var item in mainItem.Content)
            {
                // Если объект - файл
                if (!item.IsFolder)
                {
                    // Проверяем наличие соответствующей записи в CRM, а также получаем GUID родительской записи
                    var parentEntity = CheckCrmExist(mainItem);
                    // Если запись в CRM найдена
                    if (parentEntity != null)
                    {
                        // Строим путь с помощью метода из текущей функциональности
                        string folderPath = string.Empty, fullPath = string.Empty;
                        GetPathForNextCloud(item.Name, parentEntity, ref folderPath, ref fullPath);
                        fullPath = $"{baseUrl}/{fullPath}";
                        // Сравниваем фактический путь файла и сгенерированный методом
                        if (item.FullPath != fullPath)
                        {
                            // Если не совпали, то копируем файл из фактического местоположения в сгенерированное методом
                            var result = web.CopyFile(item.FullPath, fullPath);
                            if (result.Item1)
                            {
                                // Успешное копирование
                                WriteLog($"Файл успешно скопирован из {item.FullPath} в {fullPath}. ", new List<StreamWriter> { logCopy });
                                transferCount++;
                            }
                            else
                            {
                                // Неуспешное копирование
                                WriteLog($"Произошла ошибка при копирования файла из {item.FullPath} в {fullPath}: {result.Item2}", new List<StreamWriter> { logCopy });
                                failedCount++;
                            }
                        }
                        else
                        {
                            // Счетчик совпадений
                            matchedCount++;
                        }
                    }
                    else
                    {
                        // Запись, содержащая файл, не найдена в CRM 
                        WriteLog($"{item.FullPath} - Не удалось найти файл", new List<StreamWriter> { logUnfound });
                        unfoundCount++;
                    }
                    // Счетчик всего обработанных файлов
                    totalCount++;
                } 
                else
                {
                    // Переходим на следующий уровень иерархии
                    ScanFiles(item);
                }
            }
        }

        static Entity CheckCrmExist(NextCloudItem mainItem)
        {
            // Проверка на папку Обращения
            if (mainItem.Name == "Обращения")
            {
                // Получение номера Объекта недвижимости
                var code = mainItem.FullPath.Split('/')[mainItem.FullPath.Split('/').Length - 2];

                // Проверка наличия Обращений у Объекта недвижимости
                var query = new QueryExpression("incident");
                query.ColumnSet = locationManager.GetNecessaryAttributes("incident");
                var linkEntity = query.AddLink("tisa_article", "new_articleid", "tisa_articleid", JoinOperator.Inner);
                linkEntity.Columns.AddColumns("tisa_code");
                linkEntity.LinkCriteria.AddCondition("tisa_code", ConditionOperator.Equal, code);
                var result = crmServiceClient.RetrieveMultiple(query);

                // Если нашли запись возвращаем её
                if (result.Entities.Count > 0) return result[0];
            }
            else
            {
                // Получение из названия папки номера договора и даты
                var formatedName = mainItem.Name.Split(' ');
                if (formatedName.Length >= 2)
                {
                    var name = string.Join(" ", formatedName, 0, formatedName.Length - 1);
                    var date = DateTime.Parse(formatedName[formatedName.Length - 1]);

                    // Проверка наличия Договора по его номеру и дате (tisa_contractdate или createdon)
                    QueryExpression query = new QueryExpression("opportunity");
                    query.ColumnSet = locationManager.GetNecessaryAttributes("opportunity");
                    query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
                    FilterExpression orFilter = new FilterExpression(LogicalOperator.Or);
                    orFilter.AddCondition("tisa_contractdate", ConditionOperator.On, date);
                    orFilter.AddCondition("createdon", ConditionOperator.On, date);
                    query.Criteria.AddFilter(orFilter);
                    var result = crmServiceClient.RetrieveMultiple(query);

                    // Если нашли запись возвращаем её
                    if (result.Entities.Count > 0) return result[0];
                    if (name.Contains(" "))
                    {
                        name = name.Replace(" ", "/");

                        QueryExpression querySlash = new QueryExpression("opportunity");
                        querySlash.ColumnSet = locationManager.GetNecessaryAttributes("opportunity");
                        querySlash.Criteria.AddCondition("name", ConditionOperator.Equal, name);
                        querySlash.Criteria.AddFilter(orFilter);
                        result = crmServiceClient.RetrieveMultiple(querySlash);
                        if (result.Entities.Count > 0) return result[0];
                    }
                }
                else
                {
                    return null;
                }
            }
            // Иначе возвращаем null
            return null;
        }

        // Метод получения пути в NextCloud через метод из текущей функциональности
        static string GetPathForNextCloud(string filename, Entity parentEntity, ref string FolderPath, ref string FullPath)
        {
            // Переменные для работы с методами LocationManager
            bool iswholesale = false;
            Entity project = null;
            string parentFolderNameAttribute = "";
            List<(Guid Id, string FolderName)> folderPath = new List<(Guid Id, string FolderName)>();

            // Строим путь до папки связанной сущности
            locationManager.BuildFolderPath(parentEntity, ref folderPath, ref parentFolderNameAttribute, ref project, ref iswholesale);
            FolderPath = locationManager.BuildSafeFolderPathFromList(folderPath);
            // Строим для полученного пути Хранилища документов и папки в Файловом хранилище
            locationManager.BuildStorageLocations(parentEntity, locationManager.SearchParentEntity(project, ref parentFolderNameAttribute, ref project), ref folderPath, ref parentFolderNameAttribute, ref project, ref iswholesale);
            folderPath.Clear();

            // Добавляем в путь ссылку до папки связанной сущности
            string folderName = locationManager.GetFolderName(parentEntity, ref iswholesale);
            // Если наша сущность это "Оптовая сделка"
            if (iswholesale)
            {
                FolderPath += "Оптовые сделки/";
                // Проверяем и создаём запись Хранилище документов для папки "Оптовые сделки", если таковой не найдено
                if (locationManager.SearchStorageLocation(project.Id, "Оптовые сделки") == null)
                {
                    // Создание Хранилища документов "Оптовые сделки"
                    if (project != null)
                    {
                        Entity storageLocation = new Entity("crmpark_storagelocation");
                        storageLocation["subject"] = "Оптовые сделки";
                        storageLocation["regardingobjectid"] = project.ToEntityReference();
                        string storageLocationSubject = locationManager.GetFolderName(project, ref iswholesale);
                        Entity wholeSaleStorageLocationId = locationManager.SearchStorageLocation(project.Id);
                        storageLocation["crmpark_parentstoragelocationid"] = wholeSaleStorageLocationId.ToEntityReference();
                        crmServiceClient.Create(storageLocation);
                    }
                }
            }

            // Ищем запись Хранилище документов для текущей сущности и создайм её, если она отсутствует
            if (locationManager.SearchStorageLocation(parentEntity.Id, folderName, FolderPath) == null)
            {
                locationManager.CreateStorageLocation(parentEntity, folderName, ref parentFolderNameAttribute, ref project, ref iswholesale);
            }
            FolderPath += locationManager.GetFolderSafeName(folderName);

            // Создаём папку в Файловом хранилище для созданной записи сущности
            web.CreateFolderInWebDav(FolderPath);

            // Формирование полного пути до файла 
            FullPath = FolderPath + "/" + filename;
            return FullPath;
        }

        // Метод получения Примечания по названию файла и GUID родительской записи
        static Entity GetAnnotation(string fileName, Guid parentEntityId)
        {
            // Запрос на получение Примечания с тем-же названием файла, GUIDом родительской записи и входящей в интеграцию
            QueryExpression query = new QueryExpression("annotation");
            query.ColumnSet = new ColumnSet("filename", "objectid", "annotationid", "objecttypecode", "documentbody", "isdocument");
            query.Criteria.AddCondition("filename", ConditionOperator.Equal, fileName);
            query.Criteria.AddCondition("objectid", ConditionOperator.Equal, parentEntityId);
            // 3 - Договор "opportunity", 112 - Обращение "incident", 10005 - Адрес (строение) "tisa_address", 10007 - Объект недвижимости "tisa_article" 
            query.Criteria.AddCondition("objecttypecode", ConditionOperator.In, 3, 112, 10005, 10007);
            var result = crmServiceClient.RetrieveMultiple(query);
            if (result.Entities.Count > 0)
            {
                return result[0];
            }
            else
            {
                return null;
            }
        }
    }
}
