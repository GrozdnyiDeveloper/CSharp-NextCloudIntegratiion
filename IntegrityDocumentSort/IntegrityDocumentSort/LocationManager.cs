using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace IntegrityDocumentSort
{
    public class StorageLocationManager
    {
        private IOrganizationService service;
        private string baseUrl;
        private WebDavManager webDavManager;

        public StorageLocationManager(IOrganizationService service, string baseUrl, WebDavManager webDavManager)
        {
            this.service = service;
            this.baseUrl = baseUrl;
            this.webDavManager = webDavManager;
        }

        // Рекурсивный метод постройки полного пути к созданной записи сущности
        // Он проходит по иерархии файловой структуры и проверяет у найденных записей наличие записи Хранилище документов
        // Если проверка не пройдена - создаёт для них запись Хранилище документов
        public List<(Guid Id, string FolderName)> BuildFolderPath(Entity entity, ref List<(Guid Id, string FolderName)> folderPath, ref string parentFolderNameAttribute, ref Entity project, ref bool iswholesale)
        {
            // Получаем родительскую запись по иерархии у entity
            Entity parent = SearchParentEntity(entity, ref parentFolderNameAttribute, ref project);

            string folderName;
            // Если родительская запись найдена
            if (parent != null)
            {
                // Получаем название папки
                folderName = parent.GetAttributeValue<string>(parentFolderNameAttribute);

                // Если текущая запись сущности по иерархии == entity - значит это Проектная компания, заканчиваем рекурсию
                if (parent.Id == entity.Id)
                {
                    return folderPath;
                }
            }
            else
            {
                // Останавливает рекурсию и возвращает пустую строку, чтобы отправить сообщение об ошибке
                folderPath = new List<(Guid Id, string FolderName)>();
                return folderPath;
            }
            // Добавляет полученное название папки к строке полного пути к файлу созданной сущности
            folderPath.Add((parent.Id, folderName));
            // Продолжаем идти по иерархии
            return BuildFolderPath(parent, ref folderPath, ref parentFolderNameAttribute, ref project, ref iswholesale);
        }

        // Ищем родительскую запись сущности и меняем строку название для папки у родительской записи сущности
        public Entity SearchParentEntity(Entity entity, ref string parentFolderNameAttribute, ref Entity project)
        {
            Entity parent = null;
            parentFolderNameAttribute = null;

            try
            {
                // Из-за того, что у разных сущностей разные названия полей и форматы названия папок, пришлось импровизировать через switch
                switch (entity.LogicalName)
                {
                    // Договор и Оптовая сделка
                    case "opportunity":
                        // Отличаем Договор и Оптовую сделку по значению new_iswholesale или tisa_typeofsale. Если true - Оптовая сделка, false - Договор
                        if ((entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 8 ||
                             entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 16)
                             ||
                             entity.GetAttributeValue<bool>("new_iswholesale"))
                        {
                            // Ищем родительскую запись Оптовой сделки - Классификатор
                            parent = service.Retrieve("tisa_classifier", entity.GetAttributeValue<EntityReference>("new_wholesale_classificatorid").Id, GetNecessaryAttributes("tisa_classifier"));
                            parentFolderNameAttribute = "tisa_spfolder";
                        }
                        else
                        {
                            // Ищем родительскую запись Договора - Объект недвижимости
                            parent = service.Retrieve("tisa_article", entity.GetAttributeValue<EntityReference>("tisa_articleid").Id, GetNecessaryAttributes("tisa_article"));
                            parentFolderNameAttribute = "tisa_code";
                        }
                        break;
                    // Обращение
                    case "incident":
                        // Ищем родительскую запись Обращения - Объект недвижимости
                        parent = service.Retrieve("tisa_article", entity.GetAttributeValue<EntityReference>("new_articleid").Id, GetNecessaryAttributes("tisa_article"));
                        parentFolderNameAttribute = "tisa_code";
                        break;
                    // Объект недвижимости
                    case "tisa_article":
                        // Ищем родительскую запись Объекта недвижимости - Адрес (строение)
                        parent = service.Retrieve("tisa_address", entity.GetAttributeValue<EntityReference>("tisa_addressid").Id, GetNecessaryAttributes("tisa_address"));
                        parentFolderNameAttribute = "tisa_spfolder";
                        break;
                    // Адрес (строение)
                    case "tisa_address":
                        // Ищем родительскую запись Адреса (строения) - Классификатор
                        parent = service.Retrieve("tisa_classifier", entity.GetAttributeValue<EntityReference>("tisa_classifierid").Id, GetNecessaryAttributes("tisa_classifier"));
                        parentFolderNameAttribute = "tisa_spfolder";
                        break;
                    // Проект и Проектная компания
                    case "tisa_classifier":
                        // Определяем идентификатор Классификатора
                        int classifierGroup = entity.GetAttributeValue<OptionSetValue>("tisa_classifiergroup").Value;
                        switch (classifierGroup)
                        {
                            // Если значение = 50 - это Проект
                            case 50:
                                parent = service.Retrieve("tisa_classifier", entity.GetAttributeValue<EntityReference>("tisa_parentid").Id, GetNecessaryAttributes("tisa_classifier"));
                                project = entity;
                                parentFolderNameAttribute = "tisa_spfolder";
                                break;
                            // Если значение = 70 - это Проектная компания
                            case 70:
                                parent = entity;
                                parentFolderNameAttribute = "tisa_spfolder";
                                break;
                            // В остальных случаях ничего не делаем, метод вернёт null
                            default:
                                break;
                        }
                        break;
                    default:
                        break;
                }

            }
            catch (NullReferenceException ex)
            {
                throw ex;
            }

            return parent;
        }

        // Получаем название папки для entity
        public string GetFolderName(Entity entity, ref bool iswholesale)
        {
            string folderName = null;

            switch (entity.LogicalName)
            {
                // Договор и Оптовая сделка
                case "opportunity":
                    // Отличаем Договор и Оптовую сделку по значению new_iswholesale или tisa_typeofsale. Если true - Оптовая сделка, false - Договор
                    if (entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 8 ||
                    entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 16 ||
                    entity.GetAttributeValue<bool>("new_iswholesale"))
                    {
                        iswholesale = true;
                    }
                    if (entity.GetAttributeValue<DateTime>("tisa_contractdate") == DateTime.MinValue)
                    {
                        DateTime createdon = entity.GetAttributeValue<DateTime>("createdon");
                        if (createdon == DateTime.MinValue)
                        {
                            createdon = DateTime.Now;
                        }
                        folderName = entity.GetAttributeValue<string>("name") + " " + createdon.ToString("dd.MM.yyyy");
                    }
                    else
                    {
                        folderName = entity.GetAttributeValue<string>("name") + " " + entity.GetAttributeValue<DateTime>("tisa_contractdate").AddHours(5).ToString("dd.MM.yyyy");
                    }
                    break;
                // Обращение
                case "incident":
                    folderName = "Обращения";
                    break;
                // Объект недвижимости
                case "tisa_article":
                    folderName = entity.GetAttributeValue<string>("tisa_code");
                    break;
                // Адрес (строение)
                case "tisa_address":
                    folderName = entity.GetAttributeValue<string>("tisa_spfolder");
                    break;
                // Проект и Проектная компания
                case "tisa_classifier":
                    folderName = entity.GetAttributeValue<string>("tisa_spfolder");
                    break;
                default:
                    break;
            }

            return folderName;
        }

        // Возвращает ColumnSet только необходимых аттрибутов для запрашиваемой записи сущности, чтобы сэкономить ресурсы
        public ColumnSet GetNecessaryAttributes(string entityLogicalName)
        {
            ColumnSet attributes = null;

            switch (entityLogicalName)
            {
                // Договор
                case "opportunity":
                    attributes = new ColumnSet("tisa_typeofsale", "new_iswholesale", "new_wholesale_classificatorid", "tisa_articleid", "name", "createdon", "tisa_contractdate");
                    break;
                // Обращение
                case "incident":
                    attributes = new ColumnSet("new_articleid");
                    break;
                // Объект недвижимости
                case "tisa_article":
                    attributes = new ColumnSet("tisa_addressid", "tisa_code");
                    break;
                // Адрес (строение)
                case "tisa_address":
                    attributes = new ColumnSet("tisa_classifierid", "tisa_spfolder");
                    break;
                // Классификатор
                case "tisa_classifier":
                    attributes = new ColumnSet("tisa_classifiergroup", "tisa_parentid", "tisa_spfolder");
                    break;
                case "crmpark_storagelocation":
                    attributes = new ColumnSet("subject", "regardingobjectid", "crmpark_parentstoragelocationid");
                    break;
                default:
                    break;
            }

            return attributes;
        }

        public string GetFolderSafeName(string entityName)
        {
            StringBuilder nameBuilder = new StringBuilder();

            var illegalChars = Path.GetInvalidPathChars().Union(new char[] { '#', '%', ':', '?', '*', '{', '}', '<', '>', '/', '\\', '|', '&', '«', '»', '+', '~' });
            foreach (char c in entityName)
            {
                if (!illegalChars.Contains(c))
                {
                    nameBuilder.Append(c);
                }
                else
                {
                    nameBuilder.Append(" ");
                }
            }

            return nameBuilder.ToString().Trim();
        }

        public string BuildFolderPathFromList(List<string> folderPath)
        {
            string result = "";

            foreach (string folderName in folderPath)
            {
                result = $"{folderName}/{result}";
            }

            return result;
        }

        public string BuildSafeFolderPathFromList(List<(Guid Index, string FolderName)> folderPath)
        {
            string result = "";

            if (folderPath.Count > 0)
            {
                foreach ((Guid Id, string FolderName) tuple in folderPath)
                {
                    result = $"{GetFolderSafeName(tuple.FolderName)}/{result}";
                }
            }

            return result;
        }

        /// <summary>
        /// Спускаемся по построенному пути, чтобы создать папки в Файловом хранилище и записи Хранилище документов у родительских записей созданной сущности.
        /// </summary>
        public void BuildStorageLocations(Entity targetEntity, Entity currEntity, ref List<(Guid Id, string FolderName)> folderPath, ref string parentFolderNameAttribute, ref Entity project, ref bool iswholesale)
        {
            // Проверяем наличие строки каждого названия папки в Файловом хранилище
            string currentFolderPath = "";
            Entity currEntityStorageLocationId = null;
            for (int i = folderPath.Count - 1; i >= 0; i--)
            {
                // Если запись в пути не последняя
                if (i != folderPath.Count - 1)
                {
                    // То в зависимости от типа сущности получаем необходимую информацию о них
                    switch (currEntity.LogicalName)
                    {
                        // Классификатор
                        case "tisa_classifier":
                            // Определяем идентификатор Классификатора
                            int classifierGroup = currEntity.GetAttributeValue<OptionSetValue>("tisa_classifiergroup").Value;
                            switch (classifierGroup)
                            {
                                // Если значение = 50 - это Проект
                                case 50:
                                    currEntity = service.Retrieve("tisa_address", folderPath[i].Id, GetNecessaryAttributes("tisa_address"));
                                    break;
                                // Если значение = 70 - это Проектная компания
                                case 70:
                                    currEntity = service.Retrieve("tisa_classifier", folderPath[i].Id, GetNecessaryAttributes("tisa_classifier"));
                                    break;
                                // В остальных случаях ничего не делаем
                                default:
                                    break;
                            }
                            break;
                        // Адрес (строение)
                        case "tisa_address":
                            currEntity = service.Retrieve("tisa_article", folderPath[i].Id, GetNecessaryAttributes("tisa_article"));
                            break;
                        // Объект недвижимости
                        case "tisa_article":
                            if (targetEntity.LogicalName == "incident")
                            {
                                currEntity = service.Retrieve("incident", folderPath[i].Id, GetNecessaryAttributes("incident"));
                                break;
                            }
                            if (targetEntity.LogicalName == "opportunity")
                            {
                                currEntity = service.Retrieve("opportunity", folderPath[i].Id, GetNecessaryAttributes("opportunity"));
                                break;
                            }
                            break;
                        default:
                            break;
                    }
                    //currEntityStorageLocationId = SearchStorageLocation(currEntity.Id, GetFolderSafeName(folderPath[i].FolderName), $"{currentFolderPath}/");
                    currEntityStorageLocationId = SearchStorageLocation(currEntity.Id);
                    // Затем добавляем её в текущий путь
                    currentFolderPath = $"{currentFolderPath}/{GetFolderSafeName(folderPath[i].FolderName)}";
                }
                else
                {
                    //currEntityStorageLocationId = SearchStorageLocation(currEntity.Id, GetFolderSafeName(folderPath[i].FolderName), $"");
                    currEntityStorageLocationId = SearchStorageLocation(currEntity.Id);
                    // Иначе просто добавляем её в текущий путь как первую
                    currentFolderPath = folderPath[i].FolderName;
                }

                // Если у родительской записи не найдена запись Хранилище документов
                if (currEntityStorageLocationId == null)
                {
                    // Проверяем наличие папки в Файловом хранилище
                    if (!webDavManager.CheckFolderExist(currentFolderPath))
                    {
                        // При отстутствии создаём её
                        webDavManager.CreateFolderInWebDav(currentFolderPath);
                    }
                    // И создаём соответствующую запись Хранилища документов
                    CreateStorageLocation(currEntity, GetFolderSafeName(folderPath[i].FolderName), ref parentFolderNameAttribute, ref project, ref iswholesale);
                }
            }
        }

        /// <summary>
        /// Ищем запись сущности Хранилище документов в Update плагинах
        /// Отличается от SearchStorageLocation(Guid regardingObjectId, string subject)
        /// Потому что используется regardingObjectId из Post Image. И если будет изменёно название папки,
        /// то он не найдёт запись Хранилище документов, потому что в Хранилище документов указано старое название папки("subject")
        /// </summary>
        public Entity SearchStorageLocation(Guid regardingObjectId)
        {
            // Создаём запрос на поиск Хранилища документов для "Оптовые сделки"
            QueryExpression query = new QueryExpression("crmpark_storagelocation");
            query.ColumnSet = GetNecessaryAttributes("crmpark_storagelocation");
            query.Criteria.AddCondition("subject", ConditionOperator.NotEqual, "Оптовые сделки");
            query.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, regardingObjectId);
            query.Criteria.AddCondition("crmpark_parentstoragelocationid", ConditionOperator.NotNull);
            query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            EntityCollection results = service.RetrieveMultiple(query);

            // Если находим, то возвращаем найденную запись
            if (results.Entities.Count > 0)
            {
                return results.Entities[0];
            }

            // Иначе возвращаем null
            return null;
        }

        /// <summary>
        /// Поиск записи сущности Хранилище документов
        /// </summary>
        public Entity SearchStorageLocation(Guid regardingObjectId, string subject)
        {
            // Создаём запрос на поиск необходимого Хранилища документов
            QueryExpression query = new QueryExpression("crmpark_storagelocation");
            query.ColumnSet = GetNecessaryAttributes("crmpark_storagelocation");
            query.Criteria.AddCondition("subject", ConditionOperator.Equal, subject);
            query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            query.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, regardingObjectId);
            query.Criteria.AddCondition("crmpark_parentstoragelocationid", ConditionOperator.NotNull);
            EntityCollection results = service.RetrieveMultiple(query);

            // Если находим, то возвращаем найденную запись
            if (results.Entities.Count > 0)
            {
                return results.Entities[0];
            }

            // Иначе возвращаем null
            return null;
        }

        /// <summary>
        /// Поиск записи сущности Хранилище документов
        /// Если он нашёл запись сущности, то он сравнивает название папки по записи сущности и по полю subject в записи Хранилище документов
        /// Если будет несостыковка, то строим путь к папке по пройденной иерархии и по записям Хранилищ документов 
        /// и выполняем перемещение файлов в NextCloud
        /// </summary>
        public Entity SearchStorageLocation(Guid regardingObjectId, string folderName, string pathBase)
        {
            // Создаём запрос на поиск Хранилища документов для "Оптовые сделки"
            QueryExpression query = new QueryExpression("crmpark_storagelocation");
            query.ColumnSet = GetNecessaryAttributes("crmpark_storagelocation");
            query.Criteria.AddCondition("subject", ConditionOperator.NotEqual, "Оптовые сделки");
            query.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, regardingObjectId);
            query.Criteria.AddCondition("crmpark_parentstoragelocationid", ConditionOperator.NotNull);
            query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            EntityCollection results = service.RetrieveMultiple(query);

            // Если нашли запись
            if (results.Entities.Count > 0)
            {
                // То проверяем, равно ли её название названию папки текущей сущности
                if (results.Entities[0].GetAttributeValue<string>("subject") != folderName)
                {
                    // Если нет, то переименоывем её в NextCloud и в записи Хранилища документов
                    List<string> oldFolderPath = new List<string>();
                    oldFolderPath = BuildFolderPathByStorageLocations(results.Entities[0], ref oldFolderPath);
                    string oldPath = BuildFolderPathFromList(oldFolderPath);
                    string path = $"{pathBase}{folderName}";
                    webDavManager.UpdateFolderInWebDav(oldPath, path);
                    results.Entities[0]["subject"] = folderName;
                    service.Update(results.Entities[0]);
                }
                // И возвращаем полученную запись
                return results.Entities[0];
            }
            // Иначе возвращаем null
            return null;
        }

        /// <summary>
        /// Создание записи Хранилище документов
        /// </summary>
        public bool CreateStorageLocation(Entity entity, string folderName, ref string parentFolderNameAttribute, ref Entity project, ref bool iswholesale)
        {
            Entity storageLocation = new Entity("crmpark_storagelocation");

            storageLocation["subject"] = folderName;
            storageLocation["regardingobjectid"] = entity.ToEntityReference();

            // Если сущность является договором и "Оптовой сделкой"
            if (entity.LogicalName == "opportunity" && iswholesale)
            {
                // Создаём запись Хранилище документов для Оптовой сделки
                storageLocation["crmpark_parentstoragelocationid"] = SearchStorageLocation(project.Id, "Оптовые сделки").ToEntityReference();
                service.Create(storageLocation);
                return true;
            }

            // Ищем запись Хранилище документов у родительской записи по иерархии у
            // entity для заполнения поля crmpark_parentstoragelocationid
            Entity parent = SearchParentEntity(entity, ref parentFolderNameAttribute, ref project);
            if (parent != null)
            {
                // Получаем и сохраняем название папки для родительской сущности
                string parentFolder = GetFolderName(parent, ref iswholesale);
                parentFolder = GetFolderSafeName(parentFolder);

                Entity parentStorageLocationId = null;
                // Если Id родительской сущности не совпадает с текущей (то есть это не Проектная компания)
                if (parent.Id != entity.Id)
                {
                    // Ищем запись Хранилище документов у родительской записи у entity            
                    parentStorageLocationId = SearchStorageLocation(parent.Id);
                    // Если не найдена запись Хранилище документов у родительской записи - создаём для неё тоже
                    if (parentStorageLocationId == null)
                    {
                        CreateStorageLocation(parent, parentFolder, ref parentFolderNameAttribute, ref project, ref iswholesale);
                        parentStorageLocationId = SearchStorageLocation(parent.Id);
                    }
                    // Если всё равно не получилось найти родительскую запись после её создания, то ничего не делаем, НО отправим письмо администраторам
                    if (parentStorageLocationId != null)
                    {
                        storageLocation["crmpark_parentstoragelocationid"] = parentStorageLocationId.ToEntityReference();
                        service.Create(storageLocation);
                        return true;
                    }
                }
                // Иначе, если текущая запись сущности по иерархии == entity - значит это Проектная компания
                else if (parent.Id == entity.Id)
                {
                    // Запрос на storageLocation базового адреса к Файловому хранилищу;
                    parentStorageLocationId = webDavManager.GetWebDavBaseUrlRecord();
                    if (parentStorageLocationId != null)
                    {
                        // Сохраняем запись Хранилища документов для Проектной компании с ссылкой на базовый адрес
                        storageLocation["crmpark_parentstoragelocationid"] = parentStorageLocationId.ToEntityReference();
                        service.Create(storageLocation);
                        return true;
                    }
                }
            }
            // Если не находим родительскую сущность, то возвращаем false, так как запись Хранилища документов не создали
            return false;
        }

        /// <summary>
        /// Рекурсивный метод постройки пути к созданной записи сущности через запись Хранилища документов
        /// </summary>
        public List<string> BuildFolderPathByStorageLocations(Entity storageLocation, ref List<string> folderPath)
        {
            if (storageLocation == null)
            {
                // Останавливает рекурсию и возвращает пустую строку, чтобы отправить сообщение об ошибке
                folderPath = new List<string>();
                return folderPath;
            }

            // Добавляет полученное название папки к строке полного пути к файлу созданной сущности
            folderPath.Add(storageLocation.GetAttributeValue<string>("subject"));

            // Если Хранилище документов принадлежит Проектной компании, то останавливаем метод
            if (storageLocation.GetAttributeValue<EntityReference>("regardingobjectid").LogicalName == "tisa_classifier")
            {
                Entity classifier = service.Retrieve("tisa_classifier", storageLocation.GetAttributeValue<EntityReference>("regardingobjectid").Id, GetNecessaryAttributes("tisa_classifier"));
                if (classifier.GetAttributeValue<OptionSetValue>("tisa_classifiergroup").Value == 70) // 70 - проектная компания
                {
                    return folderPath;
                }
            }

            // Ищем родительскую запись Хранилища документов
            Entity parentStorageLocation = null;

            try
            {
                parentStorageLocation = service.Retrieve("crmpark_storagelocation", storageLocation.GetAttributeValue<EntityReference>("crmpark_parentstoragelocationid").Id, GetNecessaryAttributes("crmpark_storagelocation"));
            }
            catch (Exception ex)
            {
                //  вслучаи ошибки останавливает рекурсию и возвращает пустую строку, чтобы отправить сообщение об ошибке
                folderPath = new List<string>();
                return folderPath;
            }

            // Продолжаем идти по иерархии
            return BuildFolderPathByStorageLocations(parentStorageLocation, ref folderPath);
        }
    }
}
