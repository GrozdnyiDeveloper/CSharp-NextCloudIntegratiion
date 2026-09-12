using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace IntegrityDocumentSort
{
    public class WebDavManager
    {
        private IOrganizationService service;

        private string baseUrl;
        private string extendedUrl;
        private string username;
        private string password;
        //Количество доступных попыток для переименования файла 
        private const int _SET_INDEX_LIMIT = 10;

        public WebDavManager(IOrganizationService service)
        {
            this.service = service;

            var baseUrlRecord = GetWebDavBaseUrlRecord();
            if (baseUrlRecord != null)
            {
                baseUrl = baseUrlRecord.GetAttributeValue<string>("subject");
            }
            else
            {
                baseUrl = null;
            }
            extendedUrl = baseUrl;

            username = GetBNGSettingRecordKey("WebDavLogin");
            password = GetBNGSettingRecordKey("WebDavPassword");
        }

        // Выполняет поиск корневой записи Хранилища документов для получения базового адреса Файлового хранилища
        public Entity GetWebDavBaseUrlRecord()
        {
            QueryExpression query = new QueryExpression("crmpark_storagelocation");
            query.ColumnSet = new ColumnSet("subject");
            query.Criteria.AddCondition("crmpark_parentstoragelocationid", ConditionOperator.Null);

            EntityCollection results = service.RetrieveMultiple(query);

            if (results.Entities.Count > 0)
            {
                return results.Entities[0];
            }
            else
            {
                return null;
            }
        }

        // Получение значение поля Ключ из записи Параметра для подключения к Файловому хранилищу
        public string GetBNGSettingRecordKey(string bngName)
        {
            QueryExpression query = new QueryExpression("bng_setting");
            query.ColumnSet = new ColumnSet("bng_key");
            query.Criteria.AddCondition("bng_name", ConditionOperator.Equal, bngName);

            EntityCollection results = service.RetrieveMultiple(query);

            if (results.Entities.Count > 0)
            {
                Entity bngSetting = results.Entities[0];
                string result = bngSetting.GetAttributeValue<string>("bng_key");

                return result;
            }
            else
            {
                return null;
            }
        }

        // Получить содержимое папки
        public List<NextCloudItem> GetFolderContent(string folderPath, string depth)
        {
            List<NextCloudItem> fileList = new List<NextCloudItem>();
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(folderPath);
                request.Method = "PROPFIND";
                request.Headers.Add("Depth", depth); // Получаем список всех файлов и папок в каталоге
                request.Credentials = new NetworkCredential(username, password);
                request.ContentType = "application/xml";
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                string xmlBody = "<d:propfind xmlns:d='DAV:'><d:prop><d:displayname/><d:resourcetype/></d:prop></d:propfind>";
                byte[] byteArray = Encoding.UTF8.GetBytes(xmlBody);
                request.ContentLength = byteArray.Length;

                // Парсинг URL и извлечение головного адреса
                Uri uri = new Uri(folderPath);
                string baseUrl = $"{uri.Scheme}://{uri.Host}";

                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(byteArray, 0, byteArray.Length);
                }

                // Отправка запроса и получение ответа
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        if (responseStream != null)
                        {
                            using (StreamReader reader = new StreamReader(responseStream))
                            {
                                string responseContent = reader.ReadToEnd();
                                XDocument xDocument = XDocument.Parse(responseContent);
                                XNamespace dav = "DAV:";

                                foreach (var item in xDocument.Descendants(dav + "response"))
                                {
                                    var element = item.Element(dav + "propstat")?.Element(dav + "prop");
                                    if (element != null)
                                    {
                                        string name = element.Element(dav + "displayname").Value;
                                        bool isFolder = element.Element(dav + "resourcetype").Element(dav + "collection") != null;
                                        string fullPath = item.Element(dav + "href").Value;
                                        fullPath = baseUrl + WebUtility.UrlDecode(fullPath);
                                        fileList.Add(new NextCloudItem()
                                        {
                                            Name = name,
                                            IsFolder = isFolder,
                                            FullPath = fullPath.EndsWith("/") ? fullPath.Substring(0, fullPath.Length - 1) : fullPath
                                        });
                                    }
                                }
                                fileList.RemoveAt(0);
                            }
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine($"Ошибка при получении списка файлов: {ex.Message}");
            }

            return fileList;
        }

        // Метод копирования файла из одного пути в другой
        public Tuple<bool, string> CopyFile(string sourcePath, string destinationPath)
        {
            try
            {
                // Проверка наличия файла с тем-же имененм в NextCloud по расположению
                var check = CheckFile(destinationPath);
                if (check)
                {
                    return new Tuple<bool, string>(false, $"Ошибка при копирования файла: По заданному расположению уже имеется файл с тем-же именем. ");
                }

                // Копирование файла
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(sourcePath);
                request.Method = "COPY";
                request.Headers.Add("Destination", new Uri(destinationPath).AbsolutePath);
                request.Credentials = new NetworkCredential(username, password);
                request.ContentType = "application/xml";
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.NoContent)
                    {
                        return new Tuple<bool, string>(true, "");
                    }
                    else
                    {
                        return new Tuple<bool, string>(false, $"Ошибка после отправке запроса: {response.StatusCode} - {response.StatusDescription}");
                    }
                }
            }
            catch (WebException ex)
            {
                return new Tuple<bool, string>(false, $"Ошибка при копирования файла: {ex.Message}");
            }
        }
            
        // Метод проверки наличия файла по пути
        public bool CheckFile(string filePath)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(filePath);
                request.Method = "GET";
                request.Credentials = new NetworkCredential(username, password);
                request.ContentType = "application/xml";
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK )
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (WebException ex)
            {
                return false;
            }
        }

        /// <summary>
        /// Создание папки в Файловом хранилище
        /// </summary>
        public void CreateFolderInWebDav(string folderPath)
        {
            // Разделяем полный путь на строки папок, чтобы проверить наличие каждой в Файловом хранилище
            string[] folderNames = folderPath.Split('/');
            string currentFolderPath = folderNames[0];
            if (baseUrl != null)
            {
                // Для каждой папки в пути
                for (int i = 0; i < folderNames.Length; i++)
                {
                    // В текущий путь добавляем папку, если она есть
                    if (i != 0)
                    {
                        currentFolderPath = $"{currentFolderPath}/{folderNames[i]}";
                        if (string.IsNullOrWhiteSpace(folderNames[i]))
                        {
                            break;
                        }
                    }

                    // Проверяем наличие текущей папки по формируемому пути
                    bool isFolderExist = CheckFolderExist(currentFolderPath);
                    if (!isFolderExist)
                    {
                        // Если не нашли папку в NextCloud, то формируем запрос на её создание
                        string requestUrl = $"{baseUrl}/{currentFolderPath}";
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
                        request.Credentials = new NetworkCredential(username, password);
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        request.Method = "MKCOL"; // MKCOL - метод для создания новой папки
                        try
                        {
                            // Отправляем запрос
                            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                            {
                                var a = response.GetResponseStream();
                            }
                        }
                        catch (WebException ex)
                        {
                            // При возврате не корректного ответа отправляем сообщения администратору
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Проверка наличия папки в Файловом хранилище
        /// </summary>
        public bool CheckFolderExist(string folderPath)
        {
            // Формируем запрос на проверку наличия папки в Nextcloud
            string requestUrl = $"{baseUrl}/{folderPath}";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
            request.Credentials = new NetworkCredential(username, password);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            request.Method = "PROPFIND"; // PROPFIND - Метод просмотра содержимого папки
            request.ContentType = "text/xml";
            request.Headers.Add("Brief", "f");
            try
            {
                // Отправляем запрос
                using (WebResponse response = request.GetResponse())
                {
                    // Получаем статусный код ответа
                    HttpWebResponse httpResponse = (HttpWebResponse)response;
                    HttpStatusCode statusCode = httpResponse.StatusCode;

                    // Если статусный код равен 200 (ОК)
                    if (statusCode == HttpStatusCode.OK)
                    {
                        // Подтверждаем наличие папки
                        return true;
                    }
                }
            }
            catch (WebException ex)
            {
                // Если статусный код равен 404 (не найдено)
                if (ex.Response != null && ((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.NotFound)
                {
                    // Опровергаем наличие папки
                    return false;
                }
            }

            // Возвращает true, чтобы не создавать папку в Файловом хранилище по причине неожиданного результата запроса WebDav
            return true;
        }

        /// <summary>
        /// Переименование папки в хранилище путём переноса
        /// </summary>
        public void UpdateFolderInWebDav(string folderPathOld, string folderPathNew)
        {
            // Если папка по старому пути не существует, то вызываем метод её создания по новому и выходим из метода изменения
            if (!CheckFolderExist(folderPathOld))
            {
                CreateFolderInWebDav(folderPathNew);
                return;
            }

            // Вызываем метод создания папки по новому пути
            CreateFolderInWebDav(folderPathNew);

            // Декодируем все компоненты, которые не воспринимаются Uri
            string baseUrlEncoded = Uri.EscapeUriString(baseUrl);
            string encodedFolderPath = Uri.EscapeUriString(folderPathOld);
            string encodedNewFolderPath = Uri.EscapeUriString(folderPathNew);

            // Составляем полный путь запроса
            string requestUrl = $"{baseUrlEncoded}/{encodedFolderPath}";

            // Формируем запрос на переименование папок старого пути в новый (запрос посылается по старому пути)
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
            request.Credentials = new NetworkCredential(username, password);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            request.Method = "MOVE"; // MOVE - метод переименования папок путём переноса из старого пути в новый

            // Header на переименование (в нём указывается новый путь)
            string destinationHeader = $"{baseUrlEncoded}/{encodedNewFolderPath}";
            request.Headers.Add("Destination", destinationHeader);
            request.Headers.Add("Overwrite", "T");
            request.Headers.Add("Depth", "infinity");

            // Выполняем запрос на переименовая папки по построенному пути
            try
            {
                // Отправляем запрос
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                }
            }
            catch (WebException ex)
            {
                // Если статусный код равен 403 (Запрещено)
                if (ex.Response != null && ((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.Forbidden)
                {
                    return;
                }
            }
        }
    }

    public class NextCloudItem
    {
        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public string FullPath { get; set; }
        public List<NextCloudItem> Content { get; set; }
    }
}
