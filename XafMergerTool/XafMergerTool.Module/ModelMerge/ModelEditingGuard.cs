using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;

namespace XafMergerTool.Module.ModelMerge;

/// <summary>
/// Who may use the runtime model-editing actions (master-detail, variants): a role that is administrative
/// or has CanEditModel, the flag XAF's own runtime Model Editor honours (docs 403824). Merge To Module is
/// gated separately, on the developer machine.
/// </summary>
public static class ModelEditingGuard
{
    public static bool CanEditModel(XafApplication application) =>
        application.Security?.User is ISecurityUserWithRoles user &&
        user.Roles.OfType<IPermissionPolicyRole>().Any(r => r.IsAdministrative || r.CanEditModel);
}
