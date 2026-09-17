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
        db.Projects.AsNoTracking().Select(p => new ProjectRow
        {
            Id = p.Id,
            ProjectGroupId = p.ProjectGroupId,
            ProjectGroupName = db.ProjectGroups.Where(g => g.Id == p.ProjectGroupId).Select(g => g.Name).FirstOrDefault() ?? "",
            Name = p.Name,
            Description = p.Description,
            SupplierId = p.SupplierId,
            SupplierName = db.Suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.Name).FirstOrDefault(),
            Status = p.Status,
            ConfirmSide = p.ConfirmSide,
            LatestSubmissionId = p.Status == ProjectStatuses.PendingConfirmation
                ? db.ProjectStatusLogs.Where(l => l.ProjectId == p.Id && l.Action == "SUBMIT")
                    .Select(l => (ulong?)l.Id).Max()
                : null,
            CreatedBy = p.CreatedBy,
            CreatedByName = db.Users.Where(u => u.Id == p.CreatedBy).Select(u => u.RealName).FirstOrDefault(),
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            MachineModel = p.MachineModel,
            RobotVendorId = p.RobotVendorId,
            RobotVendorName = db.ProjectDictionaries.Where(d => d.Id == p.RobotVendorId).Select(d => d.Name).FirstOrDefault(),
            RobotModelId = p.RobotModelId,
            RobotModelName = db.ProjectDictionaries.Where(d => d.Id == p.RobotModelId).Select(d => d.Name).FirstOrDefault(),
            ResponsibleUserId = p.ResponsibleUserId,
            ResponsibleUserEmployeeNo = db.Users.Where(u => u.Id == p.ResponsibleUserId).Select(u => u.EmployeeNo).FirstOrDefault(),
            ResponsibleUserName = db.Users.Where(u => u.Id == p.ResponsibleUserId).Select(u => u.RealName).FirstOrDefault(),
            SectionId = p.SectionId,
            SectionName = db.Departments.Where(d => d.Id == p.SectionId && d.Kind == "SECTION").Select(d => d.Name).FirstOrDefault(),
            PriorityId = p.PriorityId,
            PriorityName = db.ProjectDictionaries.Where(d => d.Id == p.PriorityId).Select(d => d.Name).FirstOrDefault(),
            ExpectedCompletionDate = p.ExpectedCompletionDate.HasValue
                ? p.ExpectedCompletionDate.Value.ToDateTime(TimeOnly.MinValue) : null,
            HasCopyHistory = db.ProjectCopies.Any(c => c.SourceProjectId == p.Id || c.TargetProjectId == p.Id),
        });
}
