using Microsoft.EntityFrameworkCore;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// Common project read model. Callers must apply their authorized scope before
/// materialization; per-user receipts and permission-filtered lineage are not
/// part of this projection.
/// </summary>
internal static class ProjectQueries
{
    internal static IQueryable<ProjectRow> Rows(YfDbContext db) =>
        from project in db.Projects.AsNoTracking()
        join projectGroup in db.ProjectGroups on project.ProjectGroupId equals projectGroup.Id into projectGroups
        from projectGroup in projectGroups.DefaultIfEmpty()
        join supplier in db.Suppliers on project.SupplierId equals supplier.Id into suppliers
        from supplier in suppliers.DefaultIfEmpty()
        join creator in db.Users on project.CreatedBy equals creator.Id into creators
        from creator in creators.DefaultIfEmpty()
        join robotPart in db.RobotParts on project.RobotPartId equals (ulong?)robotPart.Id into robotParts
        from robotPart in robotParts.DefaultIfEmpty()
        join responsibleUser in db.Users on project.ResponsibleUserId equals (ulong?)responsibleUser.Id into responsibleUsers
        from responsibleUser in responsibleUsers.DefaultIfEmpty()
        join section in db.Departments.Where(department => department.Kind == "SECTION")
            on project.SectionId equals (ulong?)section.Id into sections
        from section in sections.DefaultIfEmpty()
        join priority in db.ProjectDictionaries on project.PriorityId equals (ulong?)priority.Id into priorities
        from priority in priorities.DefaultIfEmpty()
        select new ProjectRow
        {
            Id = project.Id,
            ProjectGroupId = project.ProjectGroupId,
            ProjectGroupName = projectGroup.Name ?? "",
            Name = project.Name,
            Description = project.Description,
            SupplierId = project.SupplierId,
            SupplierName = supplier.Name,
            Status = project.Status,
            ConfirmSide = project.ConfirmSide,
            LatestSubmissionId = project.Status == ProjectStatuses.PendingConfirmation
                ? db.ProjectStatusLogs.Where(log => log.ProjectId == project.Id && log.Action == "SUBMIT")
                    .Select(l => (ulong?)l.Id).Max()
                : null,
            CreatedBy = project.CreatedBy,
            CreatedByName = creator.RealName,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            MachineModel = project.MachineModel,
            RobotPartId = project.RobotPartId,
            RobotPartNumber = robotPart.PartNumber,
            RobotModelName = robotPart.Model ?? project.LegacyRobotModelName,
            ResponsibleUserId = project.ResponsibleUserId,
            ResponsibleUserEmployeeNo = responsibleUser.EmployeeNo,
            ResponsibleUserName = responsibleUser.RealName,
            SectionId = project.SectionId,
            SectionName = section.Name,
            PriorityId = project.PriorityId,
            PriorityName = priority.Name,
            ExpectedCompletionDate = project.ExpectedCompletionDate.HasValue
                ? project.ExpectedCompletionDate.GetValueOrDefault().ToDateTime(TimeOnly.MinValue) : null,
            HasCopyHistory = db.ProjectCopies.Any(copy =>
                copy.SourceProjectId == project.Id || copy.TargetProjectId == project.Id),
        };
}
