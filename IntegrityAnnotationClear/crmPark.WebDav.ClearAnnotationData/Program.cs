using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace crmPark.WebDav.ClearAnnotationData
{
    internal class Program
    {
        static CrmServiceClient crmServiceClient;
        static LocationManager locationManager;
        static WebDavManager web;
        static StreamWriter log;
        static int foundAnnotationsCount = 0, errorsAnnotationsCount = 0, buildPathCount = 0, foundInNextCount = 0, clearAfterFoundCount = 0, 
            createInNextCount = 0, clearAfterCreateCount = 0, unsuccessCreateCount = 0;
        static void Main(string[] args)
        {
            crmServiceClient = new CrmServiceClient(ConfigurationManager.ConnectionStrings["CRM"].ConnectionString);
            locationManager = new LocationManager(crmServiceClient, ConfigurationManager.ConnectionStrings["NextCloud"].ConnectionString);
            web = new WebDavManager(crmServiceClient);

            EntityCollection annotations = new EntityCollection();

            if (crmServiceClient.IsReady)
            {               
                try
                {
                    // Получаем даты в рамках которых проводим получение примечаний
                    string startString = ConfigurationManager.AppSettings["StartDate"];
                    DateTime start = !String.IsNullOrEmpty(startString) ? DateTime.Parse(startString) : DateTime.Parse("2024-01-01");
                    string endString = ConfigurationManager.AppSettings["EndDate"];
                    DateTime end = !String.IsNullOrEmpty(endString) ? DateTime.Parse(endString) : DateTime.Now;

                    // Открываем логи
                    log = new StreamWriter("Log.txt", true);
                    log.Write("\n\n");
                    Write("Начало работы приложения");
                    Write($"Получение примечаний с данными файлами за период от {start} до {end}.");

                    // Получаем коллекцию примечаний
                    annotations = GetAnnotationsInfo(start, end);
                    foundAnnotationsCount = annotations.Entities.Count;
                    Write($"Завершение получения примечаний. Всего получено - {foundAnnotationsCount}");
                }
                catch (Exception ex)
                {
                    // При ошибке фиксируем её в логе
                    Write("ERROR: " + ex.Message);
                    log.Close();
                    //Console.ReadLine();
                    return;
                }

                Write("----------------------------------------");
                Write("Начало обработки полученных примечаний. ");

                // Для каждого найденного в системе примечания
                foreach (Entity annotation in annotations.Entities)
                {
                    var fileName = annotation.GetAttributeValue<string>("filename");

                    try
                    {
                        Write($"Выполняем построение пути для файла {fileName}. ");

                        // Получение пути до папки связанной сущности и полного пути до самого файла
                        string folderPath = string.Empty, fullPath = string.Empty;
                        GetPathForNextCloud(annotation, ref folderPath, ref fullPath);
                        buildPathCount++;
                        Write($"Для файла {fileName} построен путь {fullPath}. ");

                        // Проверяем наличие файла в NextCloud
                        Write($"Проверяем наличие файла {fileName} в NextCloud по пути {fullPath}. ");
                        bool isFileExist = web.CheckFileExist(fullPath);
                        if (!isFileExist)
                        {
                            Write($"Файл {fileName} не был найден в NextCloud. ");

                            // Через запрос к CRM получаем данные файла
                            Write($"Получам данные файла {fileName}. ");
                            var fileBodyString = crmServiceClient.Retrieve(annotation.LogicalName, annotation.Id, new ColumnSet("documentbody")).GetAttributeValue<string>("documentbody");
                            Write($"Успешно полученны данные файла {fileName}. ");

                            // Вызов метода на отправку запроса для загрузки файла в NextCloud 
                            Write($"Производим попытку загрузки файла {fileName} в файловую систему. ");
                            bool isUploadComplete = false;
                            web.UploadFileInWebDav(folderPath, fileName, Convert.FromBase64String(fileBodyString), ref isUploadComplete);
                            if (isUploadComplete)
                            {
                                // Если отправка успешна, пишем соответствующее сообщение
                                createInNextCount++;
                                Write($"Файл {fileName} был успешно загружен в NextCloud по пути {fullPath}. ");

                                // Удаляем данные файла из CRM
                                Write($"Производим удаление данных файла {fileName} из CRM. ");
                                var annotationUpdate = new Entity(annotation.LogicalName);
                                annotationUpdate.Id = annotation.Id;
                                annotationUpdate["isdocument"] = false;
                                annotationUpdate["documentbody"] = String.Empty;
                                crmServiceClient.Update(annotationUpdate);

                                clearAfterCreateCount++;
                                Write($"Данные файла {fileName} были успешно удалены из CRM. ", "\n");
                            }
                            else
                            {
                                // Если отправка не успешна, пишем соответствующее сообщение
                                unsuccessCreateCount++;
                                Write($"Файл {fileName} не был загружен в NextCloud по пути {fullPath}. ", "\n");
                            }
                        }
                        else
                        {
                            // Если файл уже есть, то пишем соответствующее сообщение и удаляем его данные из CRM
                            foundInNextCount++;
                            Write($"Файл {fileName} был успешно найден в NextCloud по пути {fullPath}. ");

                            // Удаляем данные файла из CRM
                            Write($"Производим удаление данных файла {fileName} из CRM. ");
                            var annotationUpdate = new Entity(annotation.LogicalName);
                            annotationUpdate.Id = annotation.Id;
                            annotationUpdate["isdocument"] = false;
                            annotationUpdate["documentbody"] = String.Empty;
                            crmServiceClient.Update(annotationUpdate);

                            clearAfterFoundCount++;
                            Write($"Данные файла {fileName} были успешно удалены из CRM. ", "\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Выводим сообщение об ошибке
                        errorsAnnotationsCount++;
                        Write($"ERROR: Произошла ошибка при обработке файла {fileName}. Ошибка: {ex.Message}", "\n");
                    }
                }

                Write("----------------------------------------");

                Write($"Завершение работы приложения.", "\n" +
                    $"Всего обработано примечаний - {foundAnnotationsCount}. \n" +
                    $"Успешно построен путь - {buildPathCount}. \n" +
                    $"Успешно найдено файлов в файловой системе - {foundInNextCount}. \n" +
                    $"Успешно очищено в CRM после нахождения в файловой системе - {clearAfterFoundCount}. \n" +
                    $"Успешно создано файлов при их отсутствии в файловой системе - {createInNextCount}. \n" +
                    $"Успешно очищено в CRM после создания в файловой системе - {clearAfterCreateCount}. \n" +
                    $"Не было загружено в файловую систему - {unsuccessCreateCount}. \n" +
                    $"Ошибки - {errorsAnnotationsCount}.");

                // Обновляем даты в файле конфигурации
                var monthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                config.AppSettings.Settings["StartDate"].Value = monthStart.ToString();
                config.AppSettings.Settings["EndDate"].Value = monthStart.AddMonths(1).AddMilliseconds(-1).ToString();
                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");

                log.Close();
                //Console.ReadLine();
            }
        }

        static EntityCollection GetAnnotationsInfo(DateTime start, DateTime end, int pageNumber = 1, string pagingCookie = null)
        {
            // Переменные для пагинации (нужны, что бы обойти ограничение в 5000 записей за запрос)
            EntityCollection ResultList = new EntityCollection();
            EntityCollection ResultRecord = new EntityCollection();
            do
            {
                // Создание запроса на получение всех записей Примечания, участвующих в интеграции с NextCloud и содержащих данные за определённый срок
                QueryExpression query = new QueryExpression("annotation");
                query.ColumnSet = new ColumnSet("filename", "objectid", "annotationid", "objecttypecode");
                query.PageInfo = new PagingInfo()
                {
                    Count = 500,
                    PageNumber = pageNumber,
                    PagingCookie = pagingCookie
                };
                query.NoLock = true;
                // 3 - Договор "opportunity", 112 - Обращение "incident", 10005 - Адрес (строение) "tisa_address", 10007 - Объект недвижимости "tisa_article" 
                query.Criteria.AddCondition("objecttypecode", ConditionOperator.In, 3, 112, 10005, 10007);
                query.Criteria.AddCondition("createdon", ConditionOperator.OnOrAfter, start);
                query.Criteria.AddCondition("createdon", ConditionOperator.OnOrBefore, end);
                query.Criteria.AddCondition("documentbody", ConditionOperator.NotNull);
                ResultRecord = crmServiceClient.RetrieveMultiple(query);

                // Добавление в общую коллекцию результата текущей страницы запроса (до 5000 записей за страницу)
                ResultList.Entities.AddRange(ResultRecord.Entities);

                pageNumber++;
                pagingCookie = ResultRecord.PagingCookie;
            }
            while (ResultRecord.MoreRecords);
            // Возвращение общей коллекции всех Примечаний
            return ResultList;
        }

        static string GetPathForNextCloud(Entity annotation, ref string FolderPath, ref string FullPath)
        {
            // Переменные для работы с методами LocationManager
            bool iswholesale = false;
            Entity project = null;
            string parentFolderNameAttribute = "";
            List<(Guid Id, string FolderName)> folderPath = new List<(Guid Id, string FolderName)>();

            // Получаем данные связанной сущности
            Entity connectedEntity = locationManager.GetEntityFromAnnotation(annotation);

            // Строим путь до папки связанной сущности
            locationManager.BuildFolderPath(connectedEntity, ref folderPath, ref parentFolderNameAttribute, ref project, ref iswholesale);
            FolderPath = locationManager.BuildSafeFolderPathFromList(folderPath);
            folderPath.Clear();

            // Добавляем в путь ссылку до папки связанной сущности
            string name = locationManager.GetFolderName(connectedEntity, ref iswholesale);
            FolderPath += locationManager.GetFolderSafeName(name);

            // Формирование полного пути до файла 
            FullPath = FolderPath + "/" + annotation.GetAttributeValue<string>("filename");
            return FullPath;
        }

        static void Write(string message, string afterMessage = "")
        {
            Console.WriteLine($"{message} | {DateTime.Now}{afterMessage}");
            log.WriteLine($"{message} | {DateTime.Now}{afterMessage}");
        }
    }
}
