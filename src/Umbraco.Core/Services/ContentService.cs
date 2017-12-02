﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Umbraco.Core.Events;
using Umbraco.Core.Exceptions;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.Repositories;
using Umbraco.Core.Persistence.Repositories.Interfaces;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.Services.Changes;

namespace Umbraco.Core.Services
{
    /// <summary>
    /// Represents the Content Service, which is an easy access to operations involving <see cref="IContent"/>
    /// </summary>
    public class ContentService : ScopeRepositoryService, IContentService
    {
        private readonly MediaFileSystem _mediaFileSystem;
        private IQuery<IContent> _queryNotTrashed;

        #region Constructors

        public ContentService(IScopeUnitOfWorkProvider provider, ILogger logger, IEventMessagesFactory eventMessagesFactory, MediaFileSystem mediaFileSystem)
            : base(provider, logger, eventMessagesFactory)
        {
            _mediaFileSystem = mediaFileSystem;
        }

        #endregion

        #region Static queries

        // lazy-constructed because when the ctor runs, the query factory may not be ready

        private IQuery<IContent> QueryNotTrashed => _queryNotTrashed ?? (_queryNotTrashed = Query<IContent>().Where(x => x.Trashed == false));

        #endregion

        #region Count

        public int CountPublished(string contentTypeAlias = null)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                return repo.CountPublished();
            }
        }

        public int Count(string contentTypeAlias = null)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                return repo.Count(contentTypeAlias);
            }
        }

        public int CountChildren(int parentId, string contentTypeAlias = null)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                return repo.CountChildren(parentId, contentTypeAlias);
            }
        }

        public int CountDescendants(int parentId, string contentTypeAlias = null)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                return repo.CountDescendants(parentId, contentTypeAlias);
            }
        }

        #endregion

        #region Permissions

        /// <summary>
        /// Used to bulk update the permissions set for a content item. This will replace all permissions
        /// assigned to an entity with a list of user id & permission pairs.
        /// </summary>
        /// <param name="permissionSet"></param>
        public void SetPermissions(EntityPermissionSet permissionSet)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                repo.ReplaceContentPermissions(permissionSet);
                uow.Complete();
            }
        }

        /// <summary>
        /// Assigns a single permission to the current content item for the specified group ids
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="permission"></param>
        /// <param name="groupIds"></param>
        public void SetPermission(IContent entity, char permission, IEnumerable<int> groupIds)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                repo.AssignEntityPermission(entity, permission, groupIds);
                uow.Complete();
            }
        }

        /// <summary>
        /// Returns implicit/inherited permissions assigned to the content item for all user groups
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        public EntityPermissionCollection GetPermissions(IContent content)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                return repo.GetPermissionsForEntity(content.Id);
            }
        }

        #endregion

        #region Create

        /// <summary>
        /// Creates an <see cref="IContent"/> object using the alias of the <see cref="IContentType"/>
        /// that this Content should based on.
        /// </summary>
        /// <remarks>
        /// Note that using this method will simply return a new IContent without any identity
        /// as it has not yet been persisted. It is intended as a shortcut to creating new content objects
        /// that does not invoke a save operation against the database.
        /// </remarks>
        /// <param name="name">Name of the Content object</param>
        /// <param name="parentId">Id of Parent for the new Content</param>
        /// <param name="contentTypeAlias">Alias of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional id of the user creating the content</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent Create(string name, Guid parentId, string contentTypeAlias, int userId = 0)
        {
            var parent = GetById(parentId);
            return Create(name, parent, contentTypeAlias, userId);
        }

        /// <summary>
        /// Creates an <see cref="IContent"/> object of a specified content type.
        /// </summary>
        /// <remarks>This method simply returns a new, non-persisted, IContent without any identity. It
        /// is intended as a shortcut to creating new content objects that does not invoke a save
        /// operation against the database.
        /// </remarks>
        /// <param name="name">The name of the content object.</param>
        /// <param name="parentId">The identifier of the parent, or -1.</param>
        /// <param name="contentTypeAlias">The alias of the content type.</param>
        /// <param name="userId">The optional id of the user creating the content.</param>
        /// <returns>The content object.</returns>
        public IContent Create(string name, int parentId, string contentTypeAlias, int userId = 0)
        {
            var contentType = GetContentType(contentTypeAlias);
            if (contentType == null)
                throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias));
            var parent = parentId > 0 ? GetById(parentId) : null;
            if (parentId > 0 && parent == null)
                throw new ArgumentException("No content with that id.", nameof(parentId));

            var content = new Content(name, parentId, contentType);
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                CreateContent(uow, content, parent, userId, false);
                uow.Complete();
            }

            return content;
        }

        /// <summary>
        /// Creates an <see cref="IContent"/> object of a specified content type, at root.
        /// </summary>
        /// <remarks>This method simply returns a new, non-persisted, IContent without any identity. It
        /// is intended as a shortcut to creating new content objects that does not invoke a save
        /// operation against the database.
        /// </remarks>
        /// <param name="name">The name of the content object.</param>
        /// <param name="contentTypeAlias">The alias of the content type.</param>
        /// <param name="userId">The optional id of the user creating the content.</param>
        /// <returns>The content object.</returns>
        public IContent CreateContent(string name, string contentTypeAlias, int userId = 0)
        {
            // not locking since not saving anything

            var contentType = GetContentType(contentTypeAlias);
            if (contentType == null)
                throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias));

            var content = new Content(name, -1, contentType);
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                CreateContent(uow, content, null, userId, false);
                uow.Complete();
            }

            return content;
        }

        /// <summary>
        /// Creates an <see cref="IContent"/> object of a specified content type, under a parent.
        /// </summary>
        /// <remarks>This method simply returns a new, non-persisted, IContent without any identity. It
        /// is intended as a shortcut to creating new content objects that does not invoke a save
        /// operation against the database.
        /// </remarks>
        /// <param name="name">The name of the content object.</param>
        /// <param name="parent">The parent content object.</param>
        /// <param name="contentTypeAlias">The alias of the content type.</param>
        /// <param name="userId">The optional id of the user creating the content.</param>
        /// <returns>The content object.</returns>
        public IContent Create(string name, IContent parent, string contentTypeAlias, int userId = 0)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                // not locking since not saving anything

                var contentType = GetContentType(contentTypeAlias);
                if (contentType == null)
                    throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias)); // causes rollback

                var content = new Content(name, parent, contentType);
                CreateContent(uow, content, parent, userId, false);

                uow.Complete();
                return content;
            }
        }

        /// <summary>
        /// Creates an <see cref="IContent"/> object of a specified content type.
        /// </summary>
        /// <remarks>This method returns a new, persisted, IContent with an identity.</remarks>
        /// <param name="name">The name of the content object.</param>
        /// <param name="parentId">The identifier of the parent, or -1.</param>
        /// <param name="contentTypeAlias">The alias of the content type.</param>
        /// <param name="userId">The optional id of the user creating the content.</param>
        /// <returns>The content object.</returns>
        public IContent CreateAndSave(string name, int parentId, string contentTypeAlias, int userId = 0)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                // locking the content tree secures content types too
                uow.WriteLock(Constants.Locks.ContentTree);

                var contentType = GetContentType(contentTypeAlias); // + locks
                if (contentType == null)
                    throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias)); // causes rollback

                var parent = parentId > 0 ? GetById(parentId) : null; // + locks
                if (parentId > 0 && parent == null)
                    throw new ArgumentException("No content with that id.", nameof(parentId)); // causes rollback

                var content = parentId > 0 ? new Content(name, parent, contentType) : new Content(name, parentId, contentType);
                CreateContent(uow, content, parent, userId, true);

                uow.Complete();
                return content;
            }
        }

        /// <summary>
        /// Creates an <see cref="IContent"/> object of a specified content type, under a parent.
        /// </summary>
        /// <remarks>This method returns a new, persisted, IContent with an identity.</remarks>
        /// <param name="name">The name of the content object.</param>
        /// <param name="parent">The parent content object.</param>
        /// <param name="contentTypeAlias">The alias of the content type.</param>
        /// <param name="userId">The optional id of the user creating the content.</param>
        /// <returns>The content object.</returns>
        public IContent CreateAndSave(string name, IContent parent, string contentTypeAlias, int userId = 0)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                // locking the content tree secures content types too
                uow.WriteLock(Constants.Locks.ContentTree);

                var contentType = GetContentType(contentTypeAlias); // + locks
                if (contentType == null)
                    throw new ArgumentException("No content type with that alias.", nameof(contentTypeAlias)); // causes rollback

                var content = new Content(name, parent, contentType);
                CreateContent(uow, content, parent, userId, true);

                uow.Complete();
                return content;
            }
        }

        private void CreateContent(IScopeUnitOfWork uow, Content content, IContent parent, int userId, bool withIdentity)
        {
            // NOTE: I really hate the notion of these Creating/Created events - they are so inconsistent, I've only just found
            // out that in these 'WithIdentity' methods, the Saving/Saved events were not fired, wtf. Anyways, they're added now.
            var newArgs = parent != null
                ? new NewEventArgs<IContent>(content, content.ContentType.Alias, parent)
                : new NewEventArgs<IContent>(content, content.ContentType.Alias, -1);

            if (uow.Events.DispatchCancelable(Creating, this, newArgs))
            {
                content.WasCancelled = true;
                return;
            }

            content.CreatorId = userId;
            content.WriterId = userId;

            if (withIdentity)
            {
                var saveEventArgs = new SaveEventArgs<IContent>(content);
                if (uow.Events.DispatchCancelable(Saving, this, saveEventArgs, "Saving"))
                {
                    content.WasCancelled = true;
                    return;
                }

                var repo = uow.CreateRepository<IContentRepository>();
                repo.AddOrUpdate(content);

                uow.Flush(); // need everything so we can serialize

                saveEventArgs.CanCancel = false;
                uow.Events.Dispatch(Saved, this, saveEventArgs, "Saved");
                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, TreeChangeTypes.RefreshNode).ToEventArgs());
            }

            uow.Events.Dispatch(Created, this, new NewEventArgs<IContent>(content, false, content.ContentType.Alias, parent));

            if (withIdentity == false)
                return;

            Audit(uow, AuditType.New, $"Content '{content.Name}' was created with Id {content.Id}", content.CreatorId, content.Id);
        }

        #endregion

        #region Get, Has, Is

        /// <summary>
        /// Gets an <see cref="IContent"/> object by Id
        /// </summary>
        /// <param name="id">Id of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent GetById(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.Get(id);
            }
        }

        /// <summary>
        /// Gets an <see cref="IContent"/> object by Id
        /// </summary>
        /// <param name="ids">Ids of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IEnumerable<IContent> GetByIds(IEnumerable<int> ids)
        {
            var idsA = ids.ToArray();
            if (idsA.Length == 0) return Enumerable.Empty<IContent>();

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var items = repository.GetAll(idsA);

                var index = items.ToDictionary(x => x.Id, x => x);

                return idsA.Select(x => index.TryGetValue(x, out var c) ? c : null).WhereNotNull();
            }
        }

        /// <summary>
        /// Gets an <see cref="IContent"/> object by its 'UniqueId'
        /// </summary>
        /// <param name="key">Guid key of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IContent GetById(Guid key)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.Get(key);
            }
        }

        /// <summary>
        /// Gets <see cref="IContent"/> objects by Ids
        /// </summary>
        /// <param name="ids">Ids of the Content to retrieve</param>
        /// <returns><see cref="IContent"/></returns>
        public IEnumerable<IContent> GetByIds(IEnumerable<Guid> ids)
        {
            var idsA = ids.ToArray();
            if (idsA.Length == 0) return Enumerable.Empty<IContent>();

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var items = repository.GetAll(idsA);

                var index = items.ToDictionary(x => x.Key, x => x);

                return idsA.Select(x => index.TryGetValue(x, out var c) ? c : null).WhereNotNull();
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by the Id of the <see cref="IContentType"/>
        /// </summary>
        /// <param name="id">Id of the <see cref="IContentType"/></param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetByType(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.ContentTypeId == id);
                return repository.GetByQuery(query);
            }
        }

        internal IEnumerable<IContent> GetPublishedContentOfContentType(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.ContentTypeId == id);
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Level
        /// </summary>
        /// <param name="level">The level to retrieve Content from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        /// <remarks>Contrary to most methods, this method filters out trashed content items.</remarks>
        public IEnumerable<IContent> GetByLevel(int level)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.Level == level && x.Trashed == false);
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a specific version of an <see cref="IContent"/> item.
        /// </summary>
        /// <param name="versionId">Id of the version to retrieve</param>
        /// <returns>An <see cref="IContent"/> item</returns>
        public IContent GetVersion(int versionId)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.GetVersion(versionId);
            }
        }

        /// <summary>
        /// Gets a collection of an <see cref="IContent"/> objects versions by Id
        /// </summary>
        /// <param name="id"></param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetVersions(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.GetAllVersions(id);
            }
        }

        /// <summary>
        /// Gets a list of all version Ids for the given content item ordered so latest is first
        /// </summary>
        /// <param name="id"></param>
        /// <param name="maxRows">The maximum number of rows to return</param>
        /// <returns></returns>
        public IEnumerable<int> GetVersionIds(int id, int maxRows)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.GetVersionIds(id, maxRows);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which are ancestors of the current content.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> to retrieve ancestors for</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetAncestors(int id)
        {
            // intentionnaly not locking
            var content = GetById(id);
            return GetAncestors(content);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which are ancestors of the current content.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to retrieve ancestors for</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetAncestors(IContent content)
        {
            //null check otherwise we get exceptions
            if (content.Path.IsNullOrWhiteSpace()) return Enumerable.Empty<IContent>();

            var rootId = Constants.System.Root.ToInvariantString();
            var ids = content.Path.Split(',')
                .Where(x => x != rootId && x != content.Id.ToString(CultureInfo.InvariantCulture)).Select(int.Parse).ToArray();
            if (ids.Any() == false)
                return new List<IContent>();

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.GetAll(ids);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetChildren(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.ParentId == id);
                return repository.GetByQuery(query).OrderBy(x => x.SortOrder);
            }
        }

        /// <summary>
        /// Gets a collection of published <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <returns>An Enumerable list of published <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPublishedChildren(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.ParentId == id && x.Published);
                return repository.GetByQuery(query).OrderBy(x => x.SortOrder);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <param name="pageIndex">Page index (zero based)</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren,
            string orderBy, Direction orderDirection, string filter = "")
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var filterQuery = filter.IsNullOrWhiteSpace()
                    ? null
                    : Query<IContent>().Where(x => x.Name.Contains(filter));
                // fixme nesting uow?!
                return GetPagedChildren(id, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, true, filterQuery);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Children from</param>
        /// <param name="pageIndex">Page index (zero based)</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter"></param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPagedChildren(int id, long pageIndex, int pageSize, out long totalChildren,
            string orderBy, Direction orderDirection, bool orderBySystemField, IQuery<IContent> filter)
        {
            if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                var query = Query<IContent>();
                //if the id is System Root, then just get all - NO! does not make sense!
                //if (id != Constants.System.Root)
                query.Where(x => x.ParentId == id);
                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, orderBySystemField, filter);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="filter">Search text filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy = "Path", Direction orderDirection = Direction.Ascending, string filter = "")
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var filterQuery = filter.IsNullOrWhiteSpace()
                    ? null
                    : Query<IContent>().Where(x => x.Name.Contains(filter));
                // fixme nesting uow?
                return GetPagedDescendants(id, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, true, filterQuery);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalChildren">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter">Search filter</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPagedDescendants(int id, long pageIndex, int pageSize, out long totalChildren, string orderBy, Direction orderDirection, bool orderBySystemField, IQuery<IContent> filter)
        {
            if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                var query = Query<IContent>();
                //if the id is System Root, then just get all
                if (id != Constants.System.Root)
                {
                    var entityRepository = uow.CreateRepository<IEntityRepository>();
                    var contentPath = entityRepository.GetAllPaths(Constants.ObjectTypes.Document, id).ToArray();
                    if (contentPath.Length == 0)
                    {
                        totalChildren = 0;
                        return Enumerable.Empty<IContent>();
                    }
                    query.Where(x => x.Path.SqlStartsWith($"{contentPath[0]},", TextColumnType.NVarchar));
                }
                return repository.GetPagedResultsByQuery(query, pageIndex, pageSize, out totalChildren, orderBy, orderDirection, orderBySystemField, filter);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by its name or partial name
        /// </summary>
        /// <param name="parentId">Id of the Parent to retrieve Children from</param>
        /// <param name="name">Full or partial name of the children</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetChildren(int parentId, string name)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.ParentId == parentId && x.Name.Contains(name));
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="id">Id of the Parent to retrieve Descendants from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetDescendants(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var content = GetById(id);
                if (content == null)
                {
                    uow.Complete(); // else causes rollback
                    return Enumerable.Empty<IContent>();
                }
                var pathMatch = content.Path + ",";
                var query = Query<IContent>().Where(x => x.Id != content.Id && x.Path.StartsWith(pathMatch));
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects by Parent Id
        /// </summary>
        /// <param name="content"><see cref="IContent"/> item to retrieve Descendants from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetDescendants(IContent content)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var pathMatch = content.Path + ",";
                var query = Query<IContent>().Where(x => x.Id != content.Id && x.Path.StartsWith(pathMatch));
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets the parent of the current content as an <see cref="IContent"/> item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> to retrieve the parent from</param>
        /// <returns>Parent <see cref="IContent"/> object</returns>
        public IContent GetParent(int id)
        {
            // intentionnaly not locking
            var content = GetById(id);
            return GetParent(content);
        }

        /// <summary>
        /// Gets the parent of the current content as an <see cref="IContent"/> item.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to retrieve the parent from</param>
        /// <returns>Parent <see cref="IContent"/> object</returns>
        public IContent GetParent(IContent content)
        {
            if (content.ParentId == Constants.System.Root || content.ParentId == Constants.System.RecycleBinContent)
                return null;

            return GetById(content.ParentId);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which reside at the first level / root
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetRootContent()
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var query = Query<IContent>().Where(x => x.ParentId == Constants.System.Root);
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Gets all published content items
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<IContent> GetAllPublished()
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return repository.GetByQuery(QueryNotTrashed);
            }
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which has an expiration date less than or equal to today.
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentForExpiration()
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                return GetContentForExpiration(uow);
            }
        }

        private IEnumerable<IContent> GetContentForExpiration(IScopeUnitOfWork uow)
        {
            var repository = uow.CreateRepository<IContentRepository>();
            var query = Query<IContent>().Where(x => x.Published && x.ExpireDate <= DateTime.Now);
            return repository.GetByQuery(query);
        }

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> objects, which has a release date less than or equal to today.
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentForRelease()
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                return GetContentForRelease(uow);
            }
        }

        private IEnumerable<IContent> GetContentForRelease(IScopeUnitOfWork uow)
        {
            var repository = uow.CreateRepository<IContentRepository>();
            var query = Query<IContent>().Where(x => x.Published == false && x.ReleaseDate <= DateTime.Now);
            return repository.GetByQuery(query);
        }

        /// <summary>
        /// Gets a collection of an <see cref="IContent"/> objects, which resides in the Recycle Bin
        /// </summary>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetContentInRecycleBin()
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var bin = $"{Constants.System.Root},{Constants.System.RecycleBinContent},";
                var query = Query<IContent>().Where(x => x.Path.StartsWith(bin));
                return repository.GetByQuery(query);
            }
        }

        /// <summary>
        /// Checks whether an <see cref="IContent"/> item has any children
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/></param>
        /// <returns>True if the content has any children otherwise False</returns>
        public bool HasChildren(int id)
        {
            return CountChildren(id) > 0;
        }

        /// <summary>
        /// Checks if the passed in <see cref="IContent"/> can be published based on the anscestors publish state.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> to check if anscestors are published</param>
        /// <returns>True if the Content can be published, otherwise False</returns>
        public bool IsPathPublishable(IContent content)
        {
            // fast
            if (content.ParentId == Constants.System.Root) return true; // root content is always publishable
            if (content.Trashed) return false; // trashed content is never publishable

            // not trashed and has a parent: publishable if the parent is path-published

            int[] ids;
            if (content.HasIdentity)
            {
                // get ids from path (we have identity)
                // skip the first one that has to be -1 - and we don't care
                // skip the last one that has to be "this" - and it's ok to stop at the parent
                ids = content.Path.Split(',').Skip(1).SkipLast().Select(int.Parse).ToArray();
            }
            else
            {
                // no path yet (no identity), have to move up to parent
                // skip the first one that has to be -1 - and we don't care
                // don't skip the last one that is "parent"
                var parent = GetById(content.ParentId);
                if (parent == null) return false;
                ids = parent.Path.Split(',').Skip(1).Select(int.Parse).ToArray();
            }
            if (ids.Length == 0)
                return false;

            // if the first one is recycle bin, fail fast
            if (ids[0] == Constants.System.RecycleBinContent)
                return false;

            // fixme - move to repository?
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                var sql = uow.SqlContext.Sql(@"
                    SELECT id
                    FROM umbracoNode
                    JOIN uDocument ON umbracoNode.id=uDocument.nodeId AND uDocument.published=@0
                    WHERE umbracoNode.trashed=@1 AND umbracoNode.id IN (@2)",
                    true, false, ids);
                var x = uow.Database.Fetch<int>(sql);
                return ids.Length == x.Count;
            }
        }

        public bool IsPathPublished(IContent content)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repo = uow.CreateRepository<IContentRepository>();
                return repo.IsPathPublished(content);
            }
        }

        #endregion

        #region Save, Publish, Unpublish

        // fixme - kill all those raiseEvents

        /// <inheritdoc />
        public OperationResult Save(IContent content, int userId = 0, bool raiseEvents = true)
        {
            var publishedState = ((Content) content).PublishedState;
            if (publishedState != PublishedState.Published && publishedState != PublishedState.Unpublished)
                throw new InvalidOperationException("Cannot save a (un)publishing, use the dedicated (un)publish method.");

            var evtMsgs = EventMessagesFactory.Get();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var saveEventArgs = new SaveEventArgs<IContent>(content, evtMsgs);
                if (raiseEvents && uow.Events.DispatchCancelable(Saving, this, saveEventArgs, "Saving"))
                {
                    uow.Complete();
                    return OperationResult.Cancel(evtMsgs);
                }

                if (string.IsNullOrWhiteSpace(content.Name))
                {
                    throw new ArgumentException("Cannot save content with empty name.");
                }

                var isNew = content.IsNewEntity();

                uow.WriteLock(Constants.Locks.ContentTree);

                var repository = uow.CreateRepository<IContentRepository>();
                if (content.HasIdentity == false)
                    content.CreatorId = userId;
                content.WriterId = userId;

                repository.AddOrUpdate(content);

                if (raiseEvents)
                {
                    saveEventArgs.CanCancel = false;
                    uow.Events.Dispatch(Saved, this, saveEventArgs, "Saved");
                }
                var changeType = isNew ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode;
                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, changeType).ToEventArgs());
                Audit(uow, AuditType.Save, "Save Content performed by user", userId, content.Id);
                uow.Complete();
            }

            return OperationResult.Succeed(evtMsgs);
        }

        /// <inheritdoc />
        public OperationResult Save(IEnumerable<IContent> contents, int userId = 0, bool raiseEvents = true)
        {
            var evtMsgs = EventMessagesFactory.Get();
            var contentsA = contents.ToArray();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var saveEventArgs = new SaveEventArgs<IContent>(contentsA, evtMsgs);
                if (raiseEvents && uow.Events.DispatchCancelable(Saving, this, saveEventArgs, "Saving"))
                {
                    uow.Complete();
                    return OperationResult.Cancel(evtMsgs);
                }

                var treeChanges = contentsA.Select(x => new TreeChange<IContent>(x,
                    x.IsNewEntity() ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode));

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                foreach (var content in contentsA)
                {
                    if (content.HasIdentity == false)
                        content.CreatorId = userId;
                    content.WriterId = userId;

                    repository.AddOrUpdate(content);
                }

                if (raiseEvents)
                {
                    saveEventArgs.CanCancel = false;
                    uow.Events.Dispatch(Saved, this, saveEventArgs, "Saved");
                }
                uow.Events.Dispatch(TreeChanged, this, treeChanges.ToEventArgs());
                Audit(uow, AuditType.Save, "Bulk Save content performed by user", userId == -1 ? 0 : userId, Constants.System.Root);

                uow.Complete();
            }

            return OperationResult.Succeed(evtMsgs);
        }

        /// <inheritdoc />
        public PublishResult SaveAndPublish(IContent content, int userId = 0, bool raiseEvents = true)
        {
            var evtMsgs = EventMessagesFactory.Get();
            PublishResult result;

            if (((Content) content).PublishedState != PublishedState.Publishing && content.Published)
            {
                // already published, and values haven't changed - i.e. not changing anything
                // nothing to do
                // fixme - unless we *want* to bump dates?
                return new PublishResult(PublishResultType.SuccessAlready, evtMsgs, content);
            }

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var saveEventArgs = new SaveEventArgs<IContent>(content, evtMsgs);
                if (raiseEvents && uow.Events.DispatchCancelable(Saving, this, saveEventArgs, "Saving"))
                {
                    uow.Complete();
                    return new PublishResult(PublishResultType.FailedCancelledByEvent, evtMsgs, content);
                }

                var isNew = content.IsNewEntity();
                var changeType = isNew ? TreeChangeTypes.RefreshBranch : TreeChangeTypes.RefreshNode;
                var previouslyPublished = content.HasIdentity && content.Published;

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                // ensure that the document can be published, and publish
                // handling events, business rules, etc
                result = StrategyCanPublish(uow, content, userId, /*checkPath:*/ true, evtMsgs);
                if (result.Success)
                    result = StrategyPublish(uow, content, /*canPublish:*/ true, userId, evtMsgs);

                // save - always, even if not publishing (this is SaveAndPublish)
                if (content.HasIdentity == false)
                    content.CreatorId = userId;
                content.WriterId = userId;

                repository.AddOrUpdate(content);

                if (raiseEvents) // always
                {
                    saveEventArgs.CanCancel = false;
                    uow.Events.Dispatch(Saved, this, saveEventArgs, "Saved");
                }

                if (result.Success == false)
                {
                    uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, changeType).ToEventArgs());
                    return result;
                }

                if (isNew == false && previouslyPublished == false)
                    changeType = TreeChangeTypes.RefreshBranch; // whole branch

                // invalidate the node/branch
                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, changeType).ToEventArgs());

                uow.Events.Dispatch(Published, this, new PublishEventArgs<IContent>(content, false, false), "Published");

                // if was not published and now is... descendants that were 'published' (but
                // had an unpublished ancestor) are 're-published' ie not explicitely published
                // but back as 'published' nevertheless
                if (isNew == false && previouslyPublished == false && HasChildren(content.Id))
                {
                    var descendants = GetPublishedDescendantsLocked(uow, repository, content).ToArray();
                    uow.Events.Dispatch(Published, this, new PublishEventArgs<IContent>(descendants, false, false), "Published");
                }

                Audit(uow, AuditType.Publish, "Save and Publish performed by user", userId, content.Id);

                uow.Complete();
            }

            return result;
        }

        /// <inheritdoc />
        public PublishResult Unpublish(IContent content, int userId = 0)
        {
            var evtMsgs = EventMessagesFactory.Get();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                var newest = GetById(content.Id); // ensure we have the newest version
                if (content.VersionId != newest.VersionId) // but use the original object if it's already the newest version
                    content = newest;
                if (content.Published == false)
                {
                    uow.Complete();
                    return new PublishResult(PublishResultType.SuccessAlready, evtMsgs, content); // already unpublished
                }

                // strategy
                // fixme should we still complete the uow? don't want to rollback here!
                var attempt = StrategyCanUnpublish(uow, content, userId, evtMsgs);
                if (attempt.Success == false) return attempt; // causes rollback
                attempt = StrategyUnpublish(uow, content, true, userId, evtMsgs);
                if (attempt.Success == false) return attempt; // causes rollback

                content.WriterId = userId;
                repository.AddOrUpdate(content);

                uow.Events.Dispatch(UnPublished, this, new PublishEventArgs<IContent>(content, false, false), "UnPublished");
                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs());
                Audit(uow, AuditType.UnPublish, "UnPublish performed by user", userId, content.Id);

                uow.Complete();
            }

            return new PublishResult(PublishResultType.Success, evtMsgs, content);
        }

        /// <inheritdoc />
        public IEnumerable<PublishResult> PerformScheduledPublish()
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);

                foreach (var d in GetContentForRelease(uow))
                {
                    PublishResult result;
                    try
                    {
                        d.ReleaseDate = null;
                        d.PublishValues(); // fixme variants?
                        result = SaveAndPublish(d, d.WriterId);
                        if (result.Success == false)
                            Logger.Error<ContentService>($"Failed to publish document id={d.Id}, reason={result.Result}.");
                    }
                    catch (Exception e)
                    {
                        Logger.Error<ContentService>($"Failed to publish document id={d.Id}, an exception was thrown.", e);
                        throw;
                    }
                    yield return result;
                }
                foreach (var d in GetContentForExpiration(uow))
                {
                    try
                    {
                        d.ExpireDate = null;
                        var result = Unpublish(d, d.WriterId);
                        if (result.Success == false)
                            Logger.Error<ContentService>($"Failed to unpublish document id={d.Id}, reason={result.Result}.");
                    }
                    catch (Exception e)
                    {
                        Logger.Error<ContentService>($"Failed to unpublish document id={d.Id}, an exception was thrown.", e);
                        throw;
                    }
                }

                uow.Complete();
            }
        }

        /// <inheritdoc />
        public IEnumerable<PublishResult> SaveAndPublishBranch(IContent content, bool force, int? languageId = null, string segment = null, int userId = 0)
        {
            bool IsEditing(IContent c, int? l, string s)
                => c.Properties.Any(x => x.Values.Where(y => y.LanguageId == l && y.Segment == s).Any(y => y.EditedValue != y.PublishedValue));

            return SaveAndPublishBranch(content, force, document => IsEditing(document, languageId, segment), document => document.PublishValues(languageId, segment), userId);
        }

        /// <inheritdoc />
        public IEnumerable<PublishResult> SaveAndPublishBranch(IContent document, bool force,
            Func<IContent, bool> editing, Func<IContent, bool> publishValues, int userId = 0)
        {
            var evtMsgs = EventMessagesFactory.Get();
            var results = new List<PublishResult>();
            var publishedDocuments = new List<IContent>();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                // fixme events?!

                if (!document.HasIdentity)
                    throw new InvalidOperationException("Do not branch-publish a new document.");

                var publishedState = ((Content) document).PublishedState;
                if (publishedState == PublishedState.Publishing)
                    throw new InvalidOperationException("Do not publish values when publishing branches.");

                // deal with the branch root - if it fails, abort
                var result = SaveAndPublishBranchOne(document, repository, uow, editing, publishValues, true, publishedDocuments, evtMsgs, userId);
                results.Add(result);
                if (!result.Success) return results;

                // deal with descendants
                // if one fails, abort its branch
                var exclude = new HashSet<int>();
                foreach (var d in GetDescendants(document))
                {
                    // if parent is excluded, exclude document and ignore
                    // if not forcing, and not publishing, exclude document and ignore
                    if (exclude.Contains(d.ParentId)  ||  !force && !d.Published)
                    {
                        exclude.Add(d.Id);
                        continue;
                    }

                    // no need to check path here,
                    // 1. because we know the parent is path-published (we just published it)
                    // 2. because it would not work as nothing's been written out to the db until the uow completes
                    result = SaveAndPublishBranchOne(d, repository, uow, editing, publishValues, false, publishedDocuments, evtMsgs, userId);
                    results.Add(result);
                    if (result.Success) continue;

                    // abort branch
                    exclude.Add(d.Id);
                }

                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(document, TreeChangeTypes.RefreshBranch).ToEventArgs());
                uow.Events.Dispatch(Published, this, new PublishEventArgs<IContent>(publishedDocuments, false, false), "Published");
                Audit(uow, AuditType.Publish, "SaveAndPublishBranch performed by user", userId, document.Id);

                uow.Complete();
            }

            return results;
        }

        private PublishResult SaveAndPublishBranchOne(IContent document,
            IContentRepository repository, IScopeUnitOfWork uow,
            Func<IContent, bool> editing, Func<IContent, bool> publishValues,
            bool checkPath,
            List<IContent> publishedDocuments,
            EventMessages evtMsgs, int userId)
        {
            // if already published, and values haven't changed - i.e. not changing anything
            // nothing to do - fixme - unless we *want* to bump dates?
            if (document.Published && (editing == null || !editing(document)))
                return new PublishResult(PublishResultType.SuccessAlready, evtMsgs, document);

            // publish & check if values are valid
            if (publishValues != null && !publishValues(document))
                return new PublishResult(PublishResultType.FailedContentInvalid, evtMsgs, document);

            // check if we can publish
            var result = StrategyCanPublish(uow, document, userId, checkPath, evtMsgs);
            if (!result.Success)
                return result;

            // publish - should be successful
            var publishResult = StrategyPublish(uow, document, /*canPublish:*/ true, userId, evtMsgs);
            if (!publishResult.Success)
                throw new Exception("oops: failed to publish.");

            // save
            document.WriterId = userId;
            repository.AddOrUpdate(document);
            publishedDocuments.Add(document);
            return publishResult;
        }

        #endregion

        #region Delete

        /// <inheritdoc />
        public OperationResult Delete(IContent content, int userId)
        {
            var evtMsgs = EventMessagesFactory.Get();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var deleteEventArgs = new DeleteEventArgs<IContent>(content, evtMsgs);
                if (uow.Events.DispatchCancelable(Deleting, this, deleteEventArgs))
                {
                    uow.Complete();
                    return OperationResult.Cancel(evtMsgs);
                }

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                // if it's not trashed yet, and published, we should unpublish
                // but... UnPublishing event makes no sense (not going to cancel?) and no need to save
                // just raise the event
                if (content.Trashed == false && content.Published)
                    uow.Events.Dispatch(UnPublished, this, new PublishEventArgs<IContent>(content, false, false), "UnPublished");

                DeleteLocked(uow, repository, content);

                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, TreeChangeTypes.Remove).ToEventArgs());
                Audit(uow, AuditType.Delete, "Delete Content performed by user", userId, content.Id);

                uow.Complete();
            }

            return OperationResult.Succeed(evtMsgs);
        }

        private void DeleteLocked(IScopeUnitOfWork uow, IContentRepository repository, IContent content)
        {
            // then recursively delete descendants, bottom-up
            // just repository.Delete + an event
            var stack = new Stack<IContent>();
            stack.Push(content);
            var level = 1;
            while (stack.Count > 0)
            {
                var c = stack.Peek();
                IContent[] cc;
                if (c.Level == level)
                    while ((cc = c.Children(this).ToArray()).Length > 0)
                    {
                        foreach (var ci in cc)
                            stack.Push(ci);
                        c = cc[cc.Length - 1];
                    }
                c = stack.Pop();
                level = c.Level;

                repository.Delete(c);
                var args = new DeleteEventArgs<IContent>(c, false); // raise event & get flagged files
                uow.Events.Dispatch(Deleted, this, args);

                // fixme not going to work, do it differently
                _mediaFileSystem.DeleteFiles(args.MediaFilesToDelete, // remove flagged files
                    (file, e) => Logger.Error<ContentService>("An error occurred while deleting file attached to nodes: " + file, e));
            }
        }

        //TODO:
        // both DeleteVersions methods below have an issue. Sort of. They do NOT take care of files the way
        // Delete does - for a good reason: the file may be referenced by other, non-deleted, versions. BUT,
        // if that's not the case, then the file will never be deleted, because when we delete the content,
        // the version referencing the file will not be there anymore. SO, we can leak files.

        /// <summary>
        /// Permanently deletes versions from an <see cref="IContent"/> object prior to a specific date.
        /// This method will never delete the latest version of a content item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> object to delete versions from</param>
        /// <param name="versionDate">Latest version date</param>
        /// <param name="userId">Optional Id of the User deleting versions of a Content object</param>
        public void DeleteVersions(int id, DateTime versionDate, int userId = 0)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var deleteRevisionsEventArgs = new DeleteRevisionsEventArgs(id, dateToRetain: versionDate);
                if (uow.Events.DispatchCancelable(DeletingVersions, this, deleteRevisionsEventArgs))
                {
                    uow.Complete();
                    return;
                }

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                repository.DeleteVersions(id, versionDate);

                deleteRevisionsEventArgs.CanCancel = false;
                uow.Events.Dispatch(DeletedVersions, this, deleteRevisionsEventArgs);
                Audit(uow, AuditType.Delete, "Delete Content by version date performed by user", userId, Constants.System.Root);

                uow.Complete();
            }
        }

        /// <summary>
        /// Permanently deletes specific version(s) from an <see cref="IContent"/> object.
        /// This method will never delete the latest version of a content item.
        /// </summary>
        /// <param name="id">Id of the <see cref="IContent"/> object to delete a version from</param>
        /// <param name="versionId">Id of the version to delete</param>
        /// <param name="deletePriorVersions">Boolean indicating whether to delete versions prior to the versionId</param>
        /// <param name="userId">Optional Id of the User deleting versions of a Content object</param>
        public void DeleteVersion(int id, int versionId, bool deletePriorVersions, int userId = 0)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                if (uow.Events.DispatchCancelable(DeletingVersions, this, new DeleteRevisionsEventArgs(id, /*specificVersion:*/ versionId)))
                {
                    uow.Complete();
                    return;
                }

                if (deletePriorVersions)
                {
                    var content = GetVersion(versionId);
                    // fixme nesting uow?
                    DeleteVersions(id, content.UpdateDate, userId);
                }

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var c = repository.Get(id);
                if (c.VersionId != versionId) // don't delete the current version
                    repository.DeleteVersion(versionId);

                uow.Events.Dispatch(DeletedVersions, this, new DeleteRevisionsEventArgs(id, false,/* specificVersion:*/ versionId));
                Audit(uow, AuditType.Delete, "Delete Content by version performed by user", userId, Constants.System.Root);

                uow.Complete();
            }
        }

        #endregion

        #region Move, RecycleBin

        /// <inheritdoc />
        public OperationResult MoveToRecycleBin(IContent content, int userId)
        {
            var evtMsgs = EventMessagesFactory.Get();
            var moves = new List<Tuple<IContent, string>>();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                var originalPath = content.Path;
                var moveEventInfo = new MoveEventInfo<IContent>(content, originalPath, Constants.System.RecycleBinContent);
                var moveEventArgs = new MoveEventArgs<IContent>(evtMsgs, moveEventInfo);
                if (uow.Events.DispatchCancelable(Trashing, this, moveEventArgs))
                {
                    uow.Complete();
                    return OperationResult.Cancel(evtMsgs); // causes rollback
                }

                // if it's published we may want to force-unpublish it - that would be backward-compatible... but...
                // making a radical decision here: trashing is equivalent to moving under an unpublished node so
                // it's NOT unpublishing, only the content is now masked - allowing us to restore it if wanted
                //if (content.HasPublishedVersion)
                //{ }

                PerformMoveLocked(repository, content, Constants.System.RecycleBinContent, null, userId, moves, true);
                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs());

                var moveInfo = moves
                    .Select(x => new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId))
                    .ToArray();

                moveEventArgs.CanCancel = false;
                moveEventArgs.MoveInfoCollection = moveInfo;
                uow.Events.Dispatch(Trashed, this, moveEventArgs);
                Audit(uow, AuditType.Move, "Move Content to Recycle Bin performed by user", userId, content.Id);

                uow.Complete();
            }

            return OperationResult.Succeed(evtMsgs);
        }

        /// <summary>
        /// Moves an <see cref="IContent"/> object to a new location by changing its parent id.
        /// </summary>
        /// <remarks>
        /// If the <see cref="IContent"/> object is already published it will be
        /// published after being moved to its new location. Otherwise it'll just
        /// be saved with a new parent id.
        /// </remarks>
        /// <param name="content">The <see cref="IContent"/> to move</param>
        /// <param name="parentId">Id of the Content's new Parent</param>
        /// <param name="userId">Optional Id of the User moving the Content</param>
        public void Move(IContent content, int parentId, int userId = 0)
        {
            // if moving to the recycle bin then use the proper method
            if (parentId == Constants.System.RecycleBinContent)
            {
                MoveToRecycleBin(content, userId);
                return;
            }

            var moves = new List<Tuple<IContent, string>>();

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                var parent = parentId == Constants.System.Root ? null : GetById(parentId);
                if (parentId != Constants.System.Root && (parent == null || parent.Trashed))
                    throw new InvalidOperationException("Parent does not exist or is trashed."); // causes rollback

                var moveEventInfo = new MoveEventInfo<IContent>(content, content.Path, parentId);
                var moveEventArgs = new MoveEventArgs<IContent>(moveEventInfo);
                if (uow.Events.DispatchCancelable(Moving, this, moveEventArgs))
                {
                    uow.Complete();
                    return; // causes rollback
                }

                // if content was trashed, and since we're not moving to the recycle bin,
                // indicate that the trashed status should be changed to false, else just
                // leave it unchanged
                var trashed = content.Trashed ? false : (bool?)null;

                // if the content was trashed under another content, and so has a published version,
                // it cannot move back as published but has to be unpublished first - that's for the
                // root content, everything underneath will retain its published status
                if (content.Trashed && content.Published)
                {
                    // however, it had been masked when being trashed, so there's no need for
                    // any special event here - just change its state
                    ((Content) content).PublishedState = PublishedState.Unpublishing;
                }

                PerformMoveLocked(repository, content, parentId, parent, userId, moves, trashed);

                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch).ToEventArgs());

                var moveInfo = moves //changes
                    .Select(x => new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId))
                    .ToArray();

                moveEventArgs.MoveInfoCollection = moveInfo;
                moveEventArgs.CanCancel = false;
                uow.Events.Dispatch(Moved, this, moveEventArgs);
                Audit(uow, AuditType.Move, "Move Content performed by user", userId, content.Id);

                uow.Complete();
            }
        }

        // MUST be called from within WriteLock
        // trash indicates whether we are trashing, un-trashing, or not changing anything
        private void PerformMoveLocked(IContentRepository repository,
            IContent content, int parentId, IContent parent, int userId,
            ICollection<Tuple<IContent, string>> moves,
            bool? trash)
        {
            content.WriterId = userId;
            content.ParentId = parentId;

            // get the level delta (old pos to new pos)
            var levelDelta = parent == null
                ? 1 - content.Level + (parentId == Constants.System.RecycleBinContent ? 1 : 0)
                : parent.Level + 1 - content.Level;

            var paths = new Dictionary<int, string>();

            moves.Add(Tuple.Create(content, content.Path)); // capture original path

            // get before moving, in case uow is immediate
            var descendants = GetDescendants(content);

            // these will be updated by the repo because we changed parentId
            //content.Path = (parent == null ? "-1" : parent.Path) + "," + content.Id;
            //content.SortOrder = ((ContentRepository) repository).NextChildSortOrder(parentId);
            //content.Level += levelDelta;
            PerformMoveContentLocked(repository, content, userId, trash);

            // if uow is not immediate, content.Path will be updated only when the UOW commits,
            // and because we want it now, we have to calculate it by ourselves
            //paths[content.Id] = content.Path;
            paths[content.Id] = (parent == null ? (parentId == Constants.System.RecycleBinContent ? "-1,-20" : "-1") : parent.Path) + "," + content.Id;

            foreach (var descendant in descendants)
            {
                moves.Add(Tuple.Create(descendant, descendant.Path)); // capture original path

                // update path and level since we do not update parentId
                if (paths.ContainsKey(descendant.ParentId) == false)
                    Console.WriteLine("oops on " + descendant.ParentId + " for " + content.Path + " " + parent?.Path);
                descendant.Path = paths[descendant.Id] = paths[descendant.ParentId] + "," + descendant.Id;
                Console.WriteLine("path " + descendant.Id + " = " + paths[descendant.Id]);
                descendant.Level += levelDelta;
                PerformMoveContentLocked(repository, descendant, userId, trash);
            }
        }

        private static void PerformMoveContentLocked(IContentRepository repository, IContent content, int userId, bool? trash)
        {
            if (trash.HasValue) ((ContentBase) content).Trashed = trash.Value;
            content.WriterId = userId;
            repository.AddOrUpdate(content);
        }

        /// <summary>
        /// Empties the Recycle Bin by deleting all <see cref="IContent"/> that resides in the bin
        /// </summary>
        public void EmptyRecycleBin()
        {
            var nodeObjectType = Constants.ObjectTypes.Document;
            var deleted = new List<IContent>();
            var evtMsgs = EventMessagesFactory.Get(); // todo - and then?

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                // v7 EmptyingRecycleBin and EmptiedRecycleBin events are greatly simplified since
                // each deleted items will have its own deleting/deleted events. so, files and such
                // are managed by Delete, and not here.

                // no idea what those events are for, keep a simplified version
                var recycleBinEventArgs = new RecycleBinEventArgs(nodeObjectType);
                if (uow.Events.DispatchCancelable(EmptyingRecycleBin, this, recycleBinEventArgs))
                {
                    uow.Complete();
                    return; // causes rollback
                }

                // emptying the recycle bin means deleting whetever is in there - do it properly!
                var query = Query<IContent>().Where(x => x.ParentId == Constants.System.RecycleBinContent);
                var contents = repository.GetByQuery(query).ToArray();
                foreach (var content in contents)
                {
                    DeleteLocked(uow, repository, content);
                    deleted.Add(content);
                }

                recycleBinEventArgs.CanCancel = false;
                recycleBinEventArgs.RecycleBinEmptiedSuccessfully = true; // oh my?!
                uow.Events.Dispatch(EmptiedRecycleBin, this, recycleBinEventArgs);
                uow.Events.Dispatch(TreeChanged, this, deleted.Select(x => new TreeChange<IContent>(x, TreeChangeTypes.Remove)).ToEventArgs());
                Audit(uow, AuditType.Delete, "Empty Content Recycle Bin performed by user", 0, Constants.System.RecycleBinContent);

                uow.Complete();
            }
        }

        #endregion

        #region Others

        /// <summary>
        /// Copies an <see cref="IContent"/> object by creating a new Content object of the same type and copies all data from the current
        /// to the new copy which is returned. Recursively copies all children.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to copy</param>
        /// <param name="parentId">Id of the Content's new Parent</param>
        /// <param name="relateToOriginal">Boolean indicating whether the copy should be related to the original</param>
        /// <param name="userId">Optional Id of the User copying the Content</param>
        /// <returns>The newly created <see cref="IContent"/> object</returns>
        public IContent Copy(IContent content, int parentId, bool relateToOriginal, int userId = 0)
        {
            return Copy(content, parentId, relateToOriginal, true, userId);
        }

        /// <summary>
        /// Copies an <see cref="IContent"/> object by creating a new Content object of the same type and copies all data from the current
        /// to the new copy which is returned.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to copy</param>
        /// <param name="parentId">Id of the Content's new Parent</param>
        /// <param name="relateToOriginal">Boolean indicating whether the copy should be related to the original</param>
        /// <param name="recursive">A value indicating whether to recursively copy children.</param>
        /// <param name="userId">Optional Id of the User copying the Content</param>
        /// <returns>The newly created <see cref="IContent"/> object</returns>
        public IContent Copy(IContent content, int parentId, bool relateToOriginal, bool recursive, int userId = 0)
        {
            var copy = content.DeepCloneWithResetIdentities();
            copy.ParentId = parentId;

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var copyEventArgs = new CopyEventArgs<IContent>(content, copy, true, parentId, relateToOriginal);
                if (uow.Events.DispatchCancelable(Copying, this, copyEventArgs))
                {
                    uow.Complete();
                    return null;
                }

                // note - relateToOriginal is not managed here,
                // it's just part of the Copied event args so the RelateOnCopyHandler knows what to do
                // meaning that the event has to trigger for every copied content including descendants

                var copies = new List<Tuple<IContent, IContent>>();

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                // a copy is not published (but not really unpublishing either)
                // update the create author and last edit author
                if (copy.Published)
                    ((Content) copy).Published = false;
                copy.CreatorId = userId;
                copy.WriterId = userId;

                //get the current permissions, if there are any explicit ones they need to be copied
                var currentPermissions = GetPermissions(content);
                currentPermissions.RemoveWhere(p => p.IsDefaultPermissions);

                // save and flush because we need the ID for the recursive Copying events
                repository.AddOrUpdate(copy);

                //add permissions
                if (currentPermissions.Count > 0)
                {
                    var permissionSet = new ContentPermissionSet(copy, currentPermissions);
                    repository.AddOrUpdatePermissions(permissionSet);
                }

                uow.Flush();

                // keep track of copies
                copies.Add(Tuple.Create(content, copy));
                var idmap = new Dictionary<int, int> { [content.Id] = copy.Id };

                if (recursive) // process descendants
                {
                    foreach (var descendant in GetDescendants(content))
                    {
                        // if parent has not been copied, skip, else gets its copy id
                        if (idmap.TryGetValue(descendant.ParentId, out parentId) == false) continue;

                        var descendantCopy = descendant.DeepCloneWithResetIdentities();
                        descendantCopy.ParentId = parentId;

                        if (uow.Events.DispatchCancelable(Copying, this, new CopyEventArgs<IContent>(descendant, descendantCopy, parentId)))
                            continue;

                        // a copy is not published (but not really unpublishing either)
                        // update the create author and last edit author
                        if (descendantCopy.Published)
                            ((Content) descendantCopy).Published = false;
                        descendantCopy.CreatorId = userId;
                        descendantCopy.WriterId = userId;

                        // save and flush (see above)
                        repository.AddOrUpdate(descendantCopy);
                        uow.Flush();

                        copies.Add(Tuple.Create(descendant, descendantCopy));
                        idmap[descendant.Id] = descendantCopy.Id;
                    }
                }

                // not handling tags here, because
                // - tags should be handled by the content repository
                // - a copy is unpublished and therefore has no impact on tags in DB

                uow.Events.Dispatch(TreeChanged, this, new TreeChange<IContent>(copy, TreeChangeTypes.RefreshBranch).ToEventArgs());
                foreach (var x in copies)
                    uow.Events.Dispatch(Copied, this, new CopyEventArgs<IContent>(x.Item1, x.Item2, false, x.Item2.ParentId, relateToOriginal));
                Audit(uow, AuditType.Copy, "Copy Content performed by user", content.WriterId, content.Id);

                uow.Complete();
            }

            return copy;
        }

        /// <summary>
        /// Sends an <see cref="IContent"/> to Publication, which executes handlers and events for the 'Send to Publication' action.
        /// </summary>
        /// <param name="content">The <see cref="IContent"/> to send to publication</param>
        /// <param name="userId">Optional Id of the User issueing the send to publication</param>
        /// <returns>True if sending publication was succesfull otherwise false</returns>
        public bool SendToPublication(IContent content, int userId = 0)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var sendToPublishEventArgs = new SendToPublishEventArgs<IContent>(content);
                if (uow.Events.DispatchCancelable(SendingToPublish, this, sendToPublishEventArgs))
                {
                    uow.Complete();
                    return false;
                }

                //Save before raising event
                // fixme - nesting uow?
                Save(content, userId);

                sendToPublishEventArgs.CanCancel = false;
                uow.Events.Dispatch(SentToPublish, this, sendToPublishEventArgs);
                Audit(uow, AuditType.SendToPublish, "Send to Publish performed by user", content.WriterId, content.Id);
            }

            return true;
        }

        /// <summary>
        /// Sorts a collection of <see cref="IContent"/> objects by updating the SortOrder according
        /// to the ordering of items in the passed in <see cref="IEnumerable{T}"/>.
        /// </summary>
        /// <remarks>
        /// Using this method will ensure that the Published-state is maintained upon sorting
        /// so the cache is updated accordingly - as needed.
        /// </remarks>
        /// <param name="items"></param>
        /// <param name="userId"></param>
        /// <param name="raiseEvents"></param>
        /// <returns>True if sorting succeeded, otherwise False</returns>
        public bool Sort(IEnumerable<IContent> items, int userId = 0, bool raiseEvents = true)
        {
            var itemsA = items.ToArray();
            if (itemsA.Length == 0) return true;

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                var saveEventArgs = new SaveEventArgs<IContent>(itemsA);
                if (raiseEvents && uow.Events.DispatchCancelable(Saving, this, saveEventArgs, "Saving"))
                    return false;

                var published = new List<IContent>();
                var saved = new List<IContent>();

                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                var sortOrder = 0;

                foreach (var content in itemsA)
                {
                    // if the current sort order equals that of the content we don't
                    // need to update it, so just increment the sort order and continue.
                    if (content.SortOrder == sortOrder)
                    {
                        sortOrder++;
                        continue;
                    }

                    // else update
                    content.SortOrder = sortOrder++;
                    content.WriterId = userId;

                    // if it's published, register it, no point running StrategyPublish
                    // since we're not really publishing it and it cannot be cancelled etc
                    if (content.Published)
                        published.Add(content);

                    // save
                    saved.Add(content);
                    repository.AddOrUpdate(content);
                }

                if (raiseEvents)
                {
                    saveEventArgs.CanCancel = false;
                    uow.Events.Dispatch(Saved, this, saveEventArgs, "Saved");
                }

                uow.Events.Dispatch(TreeChanged, this, saved.Select(x => new TreeChange<IContent>(x, TreeChangeTypes.RefreshNode)).ToEventArgs());

                if (raiseEvents && published.Any())
                    uow.Events.Dispatch(Published, this, new PublishEventArgs<IContent>(published, false, false), "Published");

                Audit(uow, AuditType.Sort, "Sorting content performed by user", userId, 0);

                uow.Complete();
            }

            return true;
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Gets a collection of <see cref="IContent"/> descendants by the first Parent.
        /// </summary>
        /// <param name="content"><see cref="IContent"/> item to retrieve Descendants from</param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        internal IEnumerable<IContent> GetPublishedDescendants(IContent content)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();
                return GetPublishedDescendantsLocked(uow, repository, content).ToArray(); // ToArray important in uow!
            }
        }

        internal IEnumerable<IContent> GetPublishedDescendantsLocked(IScopeUnitOfWork uow, IContentRepository repository, IContent content)
        {
            var pathMatch = content.Path + ",";
            var query = Query<IContent>().Where(x => x.Id != content.Id && x.Path.StartsWith(pathMatch) /*&& x.Trashed == false*/);
            var contents = repository.GetByQuery(query);

            // beware! contents contains all published version below content
            // including those that are not directly published because below an unpublished content
            // these must be filtered out here

            var parents = new List<int> { content.Id };
            foreach (var c in contents)
            {
                if (parents.Contains(c.ParentId))
                {
                    yield return c;
                    parents.Add(c.Id);
                }
            }
        }

        #endregion

        #region Private Methods

        private void Audit(IUnitOfWork uow, AuditType type, string message, int userId, int objectId)
        {
            var repo = uow.CreateRepository<IAuditRepository>();
            repo.AddOrUpdate(new AuditItem(objectId, message, type, userId));
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Occurs before Delete
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteEventArgs<IContent>> Deleting;

        /// <summary>
        /// Occurs after Delete
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteEventArgs<IContent>> Deleted;

        /// <summary>
        /// Occurs before Delete Versions
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteRevisionsEventArgs> DeletingVersions;

        /// <summary>
        /// Occurs after Delete Versions
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteRevisionsEventArgs> DeletedVersions;

        /// <summary>
        /// Occurs before Save
        /// </summary>
        public static event TypedEventHandler<IContentService, SaveEventArgs<IContent>> Saving;

        /// <summary>
        /// Occurs after Save
        /// </summary>
        public static event TypedEventHandler<IContentService, SaveEventArgs<IContent>> Saved;

        /// <summary>
        /// Occurs before Create
        /// </summary>
        [Obsolete("Use the Created event instead, the Creating and Created events both offer the same functionality, Creating event has been deprecated.")]
        public static event TypedEventHandler<IContentService, NewEventArgs<IContent>> Creating;

        /// <summary>
        /// Occurs after Create
        /// </summary>
        /// <remarks>
        /// Please note that the Content object has been created, but might not have been saved
        /// so it does not have an identity yet (meaning no Id has been set).
        /// </remarks>
        public static event TypedEventHandler<IContentService, NewEventArgs<IContent>> Created;

        /// <summary>
        /// Occurs before Copy
        /// </summary>
        public static event TypedEventHandler<IContentService, CopyEventArgs<IContent>> Copying;

        /// <summary>
        /// Occurs after Copy
        /// </summary>
        public static event TypedEventHandler<IContentService, CopyEventArgs<IContent>> Copied;

        /// <summary>
        /// Occurs before Content is moved to Recycle Bin
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Trashing;

        /// <summary>
        /// Occurs after Content is moved to Recycle Bin
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Trashed;

        /// <summary>
        /// Occurs before Move
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Moving;

        /// <summary>
        /// Occurs after Move
        /// </summary>
        public static event TypedEventHandler<IContentService, MoveEventArgs<IContent>> Moved;

        /// <summary>
        /// Occurs before Rollback
        /// </summary>
        public static event TypedEventHandler<IContentService, RollbackEventArgs<IContent>> RollingBack;

        /// <summary>
        /// Occurs after Rollback
        /// </summary>
        public static event TypedEventHandler<IContentService, RollbackEventArgs<IContent>> RolledBack;

        /// <summary>
        /// Occurs before Send to Publish
        /// </summary>
        public static event TypedEventHandler<IContentService, SendToPublishEventArgs<IContent>> SendingToPublish;

        /// <summary>
        /// Occurs after Send to Publish
        /// </summary>
        public static event TypedEventHandler<IContentService, SendToPublishEventArgs<IContent>> SentToPublish;

        /// <summary>
        /// Occurs before the Recycle Bin is emptied
        /// </summary>
        public static event TypedEventHandler<IContentService, RecycleBinEventArgs> EmptyingRecycleBin;

        /// <summary>
        /// Occurs after the Recycle Bin has been Emptied
        /// </summary>
        public static event TypedEventHandler<IContentService, RecycleBinEventArgs> EmptiedRecycleBin;

        /// <summary>
        /// Occurs before publish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> Publishing;

        /// <summary>
        /// Occurs after publish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> Published;

        /// <summary>
        /// Occurs before unpublish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> UnPublishing;

        /// <summary>
        /// Occurs after unpublish
        /// </summary>
        public static event TypedEventHandler<IContentService, PublishEventArgs<IContent>> UnPublished;

        /// <summary>
        /// Occurs after change.
        /// </summary>
        internal static event TypedEventHandler<IContentService, TreeChange<IContent>.EventArgs> TreeChanged;

        /// <summary>
        /// Occurs after a blueprint has been saved.
        /// </summary>
        public static event TypedEventHandler<IContentService, SaveEventArgs<IContent>> SavedBlueprint;

        /// <summary>
        /// Occurs after a blueprint has been deleted.
        /// </summary>
        public static event TypedEventHandler<IContentService, DeleteEventArgs<IContent>> DeletedBlueprint;

        #endregion

        #region Publishing Strategies

        // ensures that a document can be published
        internal PublishResult StrategyCanPublish(IScopeUnitOfWork uow, IContent content, int userId, bool checkPath, EventMessages evtMsgs)
        {
            // raise Publishing event
            if (uow.Events.DispatchCancelable(Publishing, this, new PublishEventArgs<IContent>(content, evtMsgs)))
            {
                Logger.Info<ContentService>($"Document  \"'{content.Name}\" (id={content.Id}) cannot be published: publishing was cancelled.");
                return new PublishResult(PublishResultType.FailedCancelledByEvent, evtMsgs, content);
            }

            // ensure that the document has published values
            // either because it is 'publishing' or because it already has a published version
            if (((Content) content).PublishedState != PublishedState.Publishing && content.PublishedVersionId == 0)
            {
                Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) cannot be published: document does not have published values.");
                return new PublishResult(PublishResultType.FailedNoPublishedValues, evtMsgs, content);
            }

            // ensure that the document status is correct
            switch (content.Status)
            {
                case ContentStatus.Expired:
                    Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) cannot be published: document has expired.");
                    return new PublishResult(PublishResultType.FailedHasExpired, evtMsgs, content);

                case ContentStatus.AwaitingRelease:
                    Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) cannot be published: document is awaiting release.");
                    return new PublishResult(PublishResultType.FailedAwaitingRelease, evtMsgs, content);

                case ContentStatus.Trashed:
                    Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) cannot be published: document is trashed.");
                    return new PublishResult(PublishResultType.FailedIsTrashed, evtMsgs, content);
            }

            if (!checkPath) return new PublishResult(evtMsgs, content);

            // check if the content can be path-published
            // root content can be published
            // else check ancestors - we know we are not trashed
            var pathIsOk = content.ParentId == Constants.System.Root || IsPathPublished(GetParent(content));
            if (pathIsOk == false)
            {
                Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) cannot be published: parent is not published.");
                return new PublishResult(PublishResultType.FailedPathNotPublished, evtMsgs, content);
            }

            return new PublishResult(evtMsgs, content);
        }

        // publishes a document
        internal PublishResult StrategyPublish(IScopeUnitOfWork uow, IContent content, bool canPublish, int userId, EventMessages evtMsgs)
        {
            // note: when used at top-level, StrategyCanPublish with checkPath=true should have run already
            // and alreadyCheckedCanPublish should be true, so not checking again. when used at nested level,
            // there is no need to check the path again. so, checkPath=false in StrategyCanPublish below

            var result = canPublish
                ? new PublishResult(evtMsgs, content) // already know we can
                : StrategyCanPublish(uow, content, userId, /*checkPath:*/ false, evtMsgs); // else check

            if (result.Success == false)
                return result;

            // change state to publishing
            ((Content) content).PublishedState = PublishedState.Publishing;

            Logger.Info<ContentService>($"Content \"{content.Name}\" (id={content.Id}) has been published.");
            return result;
        }

        // ensures that a document can be unpublished
        internal PublishResult StrategyCanUnpublish(IScopeUnitOfWork uow, IContent content, int userId, EventMessages evtMsgs)
        {
            // raise UnPublishing event
            if (uow.Events.DispatchCancelable(UnPublishing, this, new PublishEventArgs<IContent>(content, evtMsgs)))
            {
                Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) cannot be unpublished: unpublishing was cancelled.");
                return new PublishResult(PublishResultType.FailedCancelledByEvent, evtMsgs, content);
            }

            return new PublishResult(evtMsgs, content);
        }

        // unpublishes a document
        internal PublishResult StrategyUnpublish(IScopeUnitOfWork uow, IContent content, bool canUnpublish, int userId, EventMessages evtMsgs)
        {
            var attempt = canUnpublish
                ? new PublishResult(evtMsgs, content) // already know we can
                : StrategyCanUnpublish(uow, content, userId, evtMsgs); // else check

            if (attempt.Success == false)
                return attempt;

            // if the document has a release date set to before now,
            // it should be removed so it doesn't interrupt an unpublish
            // otherwise it would remain released == published
            if (content.ReleaseDate.HasValue && content.ReleaseDate.Value <= DateTime.Now)
            {
                content.ReleaseDate = null;
                Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) had its release date removed, because it was unpublished.");
            }

            // change state to unpublishing
            ((Content) content).PublishedState = PublishedState.Unpublishing;

            Logger.Info<ContentService>($"Document \"{content.Name}\" (id={content.Id}) has been unpublished.");
            return attempt;
        }

        #endregion

        #region Content Types

        /// <summary>
        /// Deletes all content of specified type. All children of deleted content is moved to Recycle Bin.
        /// </summary>
        /// <remarks>
        /// <para>This needs extra care and attention as its potentially a dangerous and extensive operation.</para>
        /// <para>Deletes content items of the specified type, and only that type. Does *not* handle content types
        /// inheritance and compositions, which need to be managed outside of this method.</para>
        /// </remarks>
        /// <param name="contentTypeId">Id of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional Id of the user issueing the delete operation</param>
        public void DeleteOfTypes(IEnumerable<int> contentTypeIds, int userId = 0)
        {
            //TODO: This currently this is called from the ContentTypeService but that needs to change,
            // if we are deleting a content type, we should just delete the data and do this operation slightly differently.
            // This method will recursively go lookup every content item, check if any of it's descendants are
            // of a different type, move them to the recycle bin, then permanently delete the content items.
            // The main problem with this is that for every content item being deleted, events are raised...
            // which we need for many things like keeping caches in sync, but we can surely do this MUCH better.

            var changes = new List<TreeChange<IContent>>();
            var moves = new List<Tuple<IContent, string>>();
            var contentTypeIdsA = contentTypeIds.ToArray();

            // using an immediate uow here because we keep making changes with
            // PerformMoveLocked and DeleteLocked that must be applied immediately,
            // no point queuing operations
            //
            using (var uow = UowProvider.CreateUnitOfWork(immediate: true))
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentRepository>();

                var query = Query<IContent>().WhereIn(x => x.ContentTypeId, contentTypeIdsA);
                var contents = repository.GetByQuery(query).ToArray();

                if (uow.Events.DispatchCancelable(Deleting, this, new DeleteEventArgs<IContent>(contents)))
                {
                    uow.Complete();
                    return;
                }

                // order by level, descending, so deepest first - that way, we cannot move
                // a content of the deleted type, to the recycle bin (and then delete it...)
                foreach (var content in contents.OrderByDescending(x => x.ParentId))
                {
                    // if it's not trashed yet, and published, we should unpublish
                    // but... UnPublishing event makes no sense (not going to cancel?) and no need to save
                    // just raise the event
                    if (content.Trashed == false && content.Published)
                        uow.Events.Dispatch(UnPublished, this, new PublishEventArgs<IContent>(content, false, false), "UnPublished");

                    // if current content has children, move them to trash
                    var c = content;
                    var childQuery = Query<IContent>().Where(x => x.ParentId == c.Id);
                    var children = repository.GetByQuery(childQuery);
                    foreach (var child in children)
                    {
                        // see MoveToRecycleBin
                        PerformMoveLocked(repository, child, Constants.System.RecycleBinContent, null, userId, moves, true);
                        changes.Add(new TreeChange<IContent>(content, TreeChangeTypes.RefreshBranch));
                    }

                    // delete content
                    // triggers the deleted event (and handles the files)
                    DeleteLocked(uow, repository, content);
                    changes.Add(new TreeChange<IContent>(content, TreeChangeTypes.Remove));
                }

                var moveInfos = moves
                    .Select(x => new MoveEventInfo<IContent>(x.Item1, x.Item2, x.Item1.ParentId))
                    .ToArray();
                if (moveInfos.Length > 0)
                    uow.Events.Dispatch(Trashed, this, new MoveEventArgs<IContent>(false, moveInfos), "Trashed");
                uow.Events.Dispatch(TreeChanged, this, changes.ToEventArgs());

                Audit(uow, AuditType.Delete, $"Delete Content of Type {string.Join(",", contentTypeIdsA)} performed by user", userId, Constants.System.Root);

                uow.Complete();
            }
        }

        /// <summary>
        /// Deletes all content items of specified type. All children of deleted content item is moved to Recycle Bin.
        /// </summary>
        /// <remarks>This needs extra care and attention as its potentially a dangerous and extensive operation</remarks>
        /// <param name="contentTypeId">Id of the <see cref="IContentType"/></param>
        /// <param name="userId">Optional id of the user deleting the media</param>
        public void DeleteOfType(int contentTypeId, int userId = 0)
        {
            DeleteOfTypes(new[] { contentTypeId }, userId);
        }

        private IContentType GetContentType(IScopeUnitOfWork uow, string contentTypeAlias)
        {
            if (string.IsNullOrWhiteSpace(contentTypeAlias)) throw new ArgumentNullOrEmptyException(nameof(contentTypeAlias));

            uow.ReadLock(Constants.Locks.ContentTypes);

            var repository = uow.CreateRepository<IContentTypeRepository>();
            var query = Query<IContentType>().Where(x => x.Alias == contentTypeAlias);
            var contentType = repository.GetByQuery(query).FirstOrDefault();

            if (contentType == null)
                throw new Exception($"No ContentType matching the passed in Alias: '{contentTypeAlias}' was found"); // causes rollback

            return contentType;
        }

        private IContentType GetContentType(string contentTypeAlias)
        {
            if (string.IsNullOrWhiteSpace(contentTypeAlias)) throw new ArgumentNullOrEmptyException(nameof(contentTypeAlias));

            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                return GetContentType(uow, contentTypeAlias);
            }
        }

        #endregion

        #region Blueprints

        public IContent GetBlueprintById(int id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentBlueprintRepository>();
                var blueprint = repository.Get(id);
                if (blueprint != null)
                    ((Content) blueprint).Blueprint = true;
                return blueprint;
            }
        }

        public IContent GetBlueprintById(Guid id)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                uow.ReadLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentBlueprintRepository>();
                var blueprint = repository.Get(id);
                if (blueprint != null)
                    ((Content) blueprint).Blueprint = true;
                return blueprint;
            }
        }

        public void SaveBlueprint(IContent content, int userId = 0)
        {
            //always ensure the blueprint is at the root
            if (content.ParentId != -1)
                content.ParentId = -1;

            ((Content) content).Blueprint = true;

            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);

                if (string.IsNullOrWhiteSpace(content.Name))
                {
                    throw new ArgumentException("Cannot save content blueprint with empty name.");
                }

                var repository = uow.CreateRepository<IContentBlueprintRepository>();

                if (content.HasIdentity == false)
                {
                    content.CreatorId = userId;
                }
                content.WriterId = userId;

                repository.AddOrUpdate(content);

                uow.Events.Dispatch(SavedBlueprint, this, new SaveEventArgs<IContent>(content), "SavedBlueprint");

                uow.Complete();
            }
        }

        public void DeleteBlueprint(IContent content, int userId = 0)
        {
            using (var uow = UowProvider.CreateUnitOfWork())
            {
                uow.WriteLock(Constants.Locks.ContentTree);
                var repository = uow.CreateRepository<IContentBlueprintRepository>();
                repository.Delete(content);
                uow.Events.Dispatch(DeletedBlueprint, this, new DeleteEventArgs<IContent>(content), "DeletedBlueprint");
                uow.Complete();
            }
        }

        public IContent CreateContentFromBlueprint(IContent blueprint, string name, int userId = 0)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));

            var contentType = blueprint.ContentType;
            var content = new Content(name, -1, contentType);
            content.Path = string.Concat(content.ParentId.ToString(), ",", content.Id);

            content.CreatorId = userId;
            content.WriterId = userId;

            foreach (var property in blueprint.Properties)
                content.SetValue(property.Alias, property.GetValue());

            return content;
        }

        public IEnumerable<IContent> GetBlueprintsForContentTypes(params int[] contentTypeId)
        {
            using (var uow = UowProvider.CreateUnitOfWork(readOnly: true))
            {
                var repository = uow.CreateRepository<IContentBlueprintRepository>();

                var query = Query<IContent>();
                if (contentTypeId.Length > 0)
                {
                    query.Where(x => contentTypeId.Contains(x.ContentTypeId));
                }
                return repository.GetByQuery(query).Select(x =>
                {
                    ((Content) x).Blueprint = true;
                    return x;
                });
            }
        }

        #endregion
    }
}
