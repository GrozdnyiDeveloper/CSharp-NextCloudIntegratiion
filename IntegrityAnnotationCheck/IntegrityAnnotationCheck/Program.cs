using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Activities.Expressions;
using System.Collections.Generic;
using System.IdentityModel.Metadata;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Services.Description;
using System.Windows.Controls;
using System.Windows.Shapes;
using static System.Net.Mime.MediaTypeNames;

namespace IntegrityAnnotationCheck
{
    internal class Program
    {
        static CrmServiceClient crmServiceClient;
        static LocationManager locationManager;
        static WebDavManager web;

        static void Main(string[] args)
        {
            crmServiceClient = new CrmServiceClient(System.Configuration.ConfigurationManager.ConnectionStrings["CRM"].ConnectionString);
            locationManager = new LocationManager(crmServiceClient, System.Configuration.ConfigurationManager.ConnectionStrings["NextCloud"].ConnectionString);
            web = new WebDavManager(crmServiceClient);

            if (crmServiceClient.IsReady)
            {
                // Получаем коллекцию примечаний
                EntityCollection annotations = GetAnnotationsInfo();

                // Переменные для доступа к файлам логов и подсчёта найденных/ненайденных файлов примечаний в NextCloud
                var log1 = new StreamWriter("LogUndiscoveredFiles.txt", true);
                var log2 = new StreamWriter("LogAnnotationGUID.txt", true);
                int number = 0, detectedCount = 0, undetectedCount = 0;
                // Для каждого найденного в системе примечания
                foreach (Entity annotation in annotations.Entities)
                {
                    // Получение пути до папки связанной сущности и полного пути до самого файла
                    string folderPath = string.Empty, fullPath = string.Empty;
                    GetPathForNextCloud(annotation, ref folderPath, ref fullPath);

                    // Проверяем наличие файла в NextCloud
                    bool isFileExist = web.CheckFileExist(fullPath);
                    if (!isFileExist)
                    {
                        // Если файла не существует, то в консоль и первый лог добавляем строку информации об этом
                        string undectedInfo = $"{++number}) {annotation.GetAttributeValue<string>("filename")} - Guid примечания: {annotation.GetAttributeValue<Guid>("annotationid")} - Guid связанной сущности: {annotation.GetAttributeValue<EntityReference>("objectid").Id} - Файла нет в NextCloud по расположению {fullPath}";
                        Console.WriteLine(undectedInfo);
                        log1.WriteLine(undectedInfo);

                        //Во второй лог добавляем guid примечания
                        log2.Write($"{annotation.GetAttributeValue<Guid>("annotationid")},");

                        // Увеличиваем счётчик ненайденных файлов
                        ++undetectedCount;
                    }
                    else
                    {
                        // Иначе, просто увеличиваем счётчик найденных файлов
                        ++detectedCount;
                    }
                }
                // В конце в консоль и первый лог добавляем строку с информацией об общем количестве найденных и ненайденных в NextCloud файлов
                string finalInfo = $"Общее количество файлов в системе: {detectedCount + undetectedCount}; Файлов в NextCloud: {detectedCount}; Не найденных файлов: {undetectedCount}.";
                Console.WriteLine(finalInfo);
                log1.WriteLine(finalInfo);

                //Закрываем потоки для записи логов
                log1.Close();
                log2.Close();

                // Вызов метода отправки не найденных файлов из примечаний в NextCloud
                SendFilesFromAnnotations();//2 часа 15 минут для скана 2-ух месяцев
                Console.WriteLine("Выполнение приложения завершено");
                Console.ReadLine();
            }
        }

        static void SendFilesFromAnnotations()
        {
            // Скан содержимого 2 лога с GUID-ами всех примечаний, для которых не был найден файл в NextCloud и сохранение их в виде массива с разделителем <Запятая>
            var log2 = new StreamReader("LogAnnotationGUID.txt");
            string[] GUIDs = log2.ReadToEnd().Split(',');
            log2.Close();

            // Лог для хранения неудачных переносов
            var log3 = new StreamWriter("LogFailedTransfers.txt");

            // Для каждого GUID примечания из 2 лога
            foreach (string GUID in GUIDs)
            {
                try
                {
                    // Получаем примечание по GUID
                    Entity annotation = GetAnnotation(Guid.Parse(GUID));

                    // Получение пути до папки связанной сущности и полного пути до самого файла 
                    string folderPath = string.Empty, fullPath = string.Empty;
                    GetPathForNextCloud(annotation, ref folderPath, ref fullPath);

                    if (annotation.GetAttributeValue<string>("documentbody") != null)
                    {
                        // Вызов метода на отправку запроса для загрузки файла в NextCloud 
                        bool isUploadComplete = false;
                        web.UploadFileInWebDav(folderPath, annotation.GetAttributeValue<string>("filename"), Convert.FromBase64String(annotation.GetAttributeValue<string>("documentbody")), ref isUploadComplete);
                        if (isUploadComplete)
                        {
                            // Если отправка успешна, пишем в консоль соответствующее сообщение
                            Console.WriteLine($"Файл {annotation.GetAttributeValue<string>("filename")}, из примечания {GUID}, успешно перенесён в Файловое хранилище по пути: {fullPath}. ");
                            // Удаляем файл из CRM
                            annotation["isdocument"] = false;
                            annotation["documentbody"] = String.Empty;
                            crmServiceClient.Update(annotation);
                        }
                        else
                        {
                            // Если отправка не успешна, пишем в консоль соответствующее сообщениеы
                            string failedLog = $"Файл {annotation.GetAttributeValue<string>("filename")}, из примечания {GUID}, не удалось перенести в Файловое хранилище по пути: {fullPath}. ";
                            Console.WriteLine(failedLog);
                            // И сохраняем его в лог
                            log3.WriteLine(failedLog);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Если отправка не успешна, пишем в консоль соответствующее сообщениеы
                    string failedLog = $"При отправке файла из примечания {GUID} возникла ошибка {ex.Message}. ";
                    Console.WriteLine(failedLog);
                    // И сохраняем его в лог
                    log3.WriteLine(failedLog);
                }
            }
            log3.Close();
        }

        static EntityCollection GetAnnotationsInfo(int pageNumber = 1, string pagingCookie = null)
        {
            // Переменные для пагинации (нужны, что бы обойти ограничение в 5000 записей за запрос)
            EntityCollection ResultList = new EntityCollection();
            EntityCollection ResultRecord = new EntityCollection();
            do
            {
                // Создание запроса на получение всех записей Примечания, участвующих в интеграции с NextCloud и добавленных начиная с мая 2024
                QueryExpression query = new QueryExpression("annotation");
                query.ColumnSet = new ColumnSet("filename", "objectid", "annotationid", "objecttypecode");
                query.PageInfo = new PagingInfo()
                {
                    Count = 5000,
                    PageNumber = pageNumber,
                    PagingCookie = pagingCookie
                };
                query.NoLock = true;
                // 3 - Договор "opportunity", 112 - Обращение "incident", 10005 - Адрес (строение) "tisa_address", 10007 - Объект недвижимости "tisa_article" 
                query.Criteria.AddCondition("objecttypecode", ConditionOperator.In, 3, 112, 10005, 10007);
                query.Criteria.AddCondition("createdon", ConditionOperator.OnOrAfter, "2025-01-01");
                query.Criteria.AddCondition("createdon", ConditionOperator.OnOrBefore, "2025-02-01");
                query.Criteria.AddCondition("documentbody", ConditionOperator.NotNull);
                //query.Criteria.AddCondition("filename", ConditionOperator.NotNull);
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

        static Entity GetAnnotation(Guid guid)
        {
            // Создание запроса на получение Примечания по GUID 
            QueryExpression query = new QueryExpression("annotation");
            query.ColumnSet = new ColumnSet("filename", "objectid", "annotationid", "objecttypecode", "documentbody", "isdocument");
            query.Criteria.AddCondition("annotationid", ConditionOperator.Equal, guid);

            return crmServiceClient.RetrieveMultiple(query).Entities.FirstOrDefault();
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
    }
}
