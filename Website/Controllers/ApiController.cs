using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Data.Entity;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Web.Mvc;
using System.Web.UI;
using Ninject;
using NuGet;

namespace NuGetGallery
{
	public partial class ApiController : AppController
    {
        private readonly IPackageService packageSvc;
        private readonly IUserService userSvc;
        private readonly IPackageFileService packageFileSvc;
        private readonly INuGetExeDownloaderService nugetExeDownloaderSvc;

        public ApiController(IPackageService packageSvc,
                             IPackageFileService packageFileSvc,
                             IUserService userSvc,
                             INuGetExeDownloaderService nugetExeDownloaderSvc)
        {
            this.packageSvc = packageSvc;
            this.packageFileSvc = packageFileSvc;
            this.userSvc = userSvc;
            this.nugetExeDownloaderSvc = nugetExeDownloaderSvc;
        }

        [ActionName("GetPackageCdnApi"), HttpGet]
        public virtual ActionResult GetPackageCdn(string id, string version)
        {
            return GetPackage(id, version, useCdn: true);
        }

        [ActionName("GetPackageApi"), HttpGet]
        public virtual ActionResult GetPackage(string id, string version)
        {
            return GetPackage(id, version, useCdn: false);
        }

        private ActionResult GetPackage(string id, string version, bool useCdn)
        {
            // if the version is null, the user is asking for the latest version. Presumably they don't want includePrerelease release versions. 
            // The allow prerelease flag is ignored if both partialId and version are specified.
            var package = packageSvc.FindPackageByIdAndVersion(id, version, allowPrerelease: false);

            if (package == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));

            packageSvc.AddDownloadStatistics(package,
                                             Request.UserHostAddress,
                                             Request.UserAgent);

            if (!string.IsNullOrWhiteSpace(package.ExternalPackageUrl))
                return Redirect(package.ExternalPackageUrl);
            else
            {
                return packageFileSvc.CreateDownloadPackageActionResult(package, useCdn);
            }
        }

        [ActionName("GetNuGetExeApi"),
         HttpGet,
         OutputCache(VaryByParam = "none", Location = OutputCacheLocation.ServerAndClient, Duration = 600)]
        public virtual ActionResult GetNuGetExe()
        {
            return nugetExeDownloaderSvc.CreateNuGetExeDownloadActionResult();
        }

        public class DependentsInput
        {
            public string id { get; set; }
            public string version { get; set; }
        }

        public class DependentsOutput
        {
            public DependentsInput src { get; set; }

            public IEnumerable<DependentsInput> dependents { get; set; }
        }

        [ActionName("GetDependents")]
        public virtual JsonResult GetDependents(DependentsInput src)
        {
            var packageRepo = Container.Kernel.Get<IEntityRepository<Package>>();
            if (String.IsNullOrEmpty(src.version))
            {
                var latestPackage = packageRepo.GetAll().Include(p => p.PackageRegistration)
                                                        .First(p => p.PackageRegistration.Id == src.id && p.IsLatest);
                src.version = latestPackage.Version;
            }

            var output = new List<DependentsOutput>();

                var semVer = SemanticVersion.Parse(src.version);
                

                var dependents = packageRepo.GetAll()
                                              .Include(p => p.PackageRegistration)
                                              .Include(p => p.Dependencies)
                                              .Where(package => package.IsLatest && package.Dependencies.Any(d => d.Id == src.id))
                                              .OrderByDescending(p => p.PackageRegistration.DownloadCount)
                                              .Take(20)
                                              .AsEnumerable();

            var list = new List<DependentsInput>();
            foreach(var item in dependents)
            {
                var dependency = item.Dependencies.First(d => d.Id.Equals(src.id, StringComparison.OrdinalIgnoreCase));
                if (String.IsNullOrEmpty(dependency.VersionSpec) || ParseVSpec(dependency).Satisfies(semVer))
                {
                    list.Add(new DependentsInput { id = item.PackageRegistration.Id, version = item.Version });
                }
            }
            output.Add(new DependentsOutput { src = src, dependents = list });
            return new JsonResult { Data = output };
        }

        private static IVersionSpec ParseVSpec(PackageDependency d)
        {
            return VersionUtility.ParseVersionSpec(d.VersionSpec);
        }

        [ActionName("VerifyPackageKeyApi"), HttpGet]
        public virtual ActionResult VerifyPackageKey(string apiKey, string id, string version)
        {
            Guid parsedApiKey;
            if (!Guid.TryParse(apiKey, out parsedApiKey))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, string.Format(CultureInfo.CurrentCulture, Strings.InvalidApiKey, apiKey));
            }

            var user = userSvc.FindByApiKey(parsedApiKey);
            if (user == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));

            if (!String.IsNullOrEmpty(id))
            {
                // If the partialId is present, then verify that the user has permission to push for the specific Id \ version combination.
                var package = packageSvc.FindPackageByIdAndVersion(id, version);
                if (package == null)
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));

                if (!package.IsOwner(user))
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));
            }

            return new EmptyResult();
        }

        [ActionName("PushPackageApi"), HttpPut]
        public virtual ActionResult CreatePackagePut(string apiKey)
        {
            return CreatePackageInternal(apiKey);
        }

        [ActionName("PushPackageApi"), HttpPost]
        public virtual ActionResult CreatePackagePost(string apiKey)
        {
            return CreatePackageInternal(apiKey);
        }

        private ActionResult CreatePackageInternal(string apiKey)
        {
            Guid parsedApiKey;
            if (!Guid.TryParse(apiKey, out parsedApiKey))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, string.Format(CultureInfo.CurrentCulture, Strings.InvalidApiKey, apiKey));
            }

            var user = userSvc.FindByApiKey(parsedApiKey);
            if (user == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, String.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));

            var packageToPush = ReadPackageFromRequest();

            // Ensure that the user can push packages for this partialId.
            var packageRegistration = packageSvc.FindPackageRegistrationById(packageToPush.Id);
            if (packageRegistration != null)
            {
                if (!packageRegistration.IsOwner(user))
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, String.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));
                }

                // Check if a particular Id-Version combination already exists. We eventually need to remove this check.
                bool packageExists = packageRegistration.Packages.Any(p => p.Version.Equals(packageToPush.Version.ToString(), StringComparison.OrdinalIgnoreCase));
                if (packageExists)
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Conflict,
                        String.Format(CultureInfo.CurrentCulture, Strings.PackageExistsAndCannotBeModified, packageToPush.Id, packageToPush.Version.ToString()));
                }
            }

            var package = packageSvc.CreatePackage(packageToPush, user);
            if (packageToPush.Id.Equals(Constants.NuGetCommandLinePackageId, StringComparison.OrdinalIgnoreCase) && package.IsLatestStable)
            {
                // If we're pushing a new stable version of NuGet.CommandLine, update the extracted executable.
                nugetExeDownloaderSvc.UpdateExecutable(packageToPush);
            }

            return new HttpStatusCodeResult(201);
        }

        [ActionName("DeletePackageApi"), HttpDelete]
        public virtual ActionResult DeletePackage(string apiKey, string id, string version)
        {
            Guid parsedApiKey;
            if (!Guid.TryParse(apiKey, out parsedApiKey))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, string.Format(CultureInfo.CurrentCulture, Strings.InvalidApiKey, apiKey));
            }

            var user = userSvc.FindByApiKey(parsedApiKey);
            if (user == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "delete"));

            var package = packageSvc.FindPackageByIdAndVersion(id, version);
            if (package == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));

            if (!package.IsOwner(user))
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "delete"));

            packageSvc.MarkPackageUnlisted(package);
            return new EmptyResult();
        }

        [ActionName("PublishPackageApi"), HttpPost]
        public virtual ActionResult PublishPackage(string apiKey, string id, string version)
        {
            return new EmptyResult();
        }

        protected override void OnException(ExceptionContext filterContext)
        {
            filterContext.ExceptionHandled = true;
            var exception = filterContext.Exception;
            var request = filterContext.HttpContext.Request;
            filterContext.Result = new HttpStatusCodeWithBodyResult(HttpStatusCode.InternalServerError, exception.Message, request.IsLocal ? exception.StackTrace : exception.Message);
        }

        protected internal virtual IPackage ReadPackageFromRequest()
        {
            Stream stream;
            if (Request.Files.Count > 0)
            {
                // If we're using the newer API, the package stream is sent as a file.
                stream = Request.Files[0].InputStream;
            }
            else
                stream = Request.InputStream;

            return new ZipPackage(stream);
        }

        [ActionName("PackageIDs"), HttpGet]
        public virtual ActionResult GetPackageIds(
            string partialId,
            bool? includePrerelease)
        {
            var qry = GetService<IPackageIdsQuery>();
            return new JsonNetResult(qry.Execute(partialId, includePrerelease).ToArray());
        }

        [ActionName("PackageVersions"), HttpGet]
        public virtual ActionResult GetPackageVersions(
            string id,
            bool? includePrerelease)
        {
            var qry = GetService<IPackageVersionsQuery>();
            return new JsonNetResult(qry.Execute(id, includePrerelease).ToArray());
        }
    }
}