using CrmEarlyBound;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.Query;
namespace Plugins
{
    public class RecordGuard : PluginBase
    {
        public RecordGuard() : base(typeof(RecordGuard))
        {
        }

        protected override void ExecuteCdsPlugin(ILocalPluginContext localPluginContext)
        {
            var context = localPluginContext.PluginExecutionContext;
            var target = (Entity)context.InputParameters["Target"];

            switch (context.PrimaryEntityName)
            {
                #region Validate Tenancy
                case pc_Tenancy.EntityLogicalName:
                    {
                        var previous = context.MessageName == "Update"
                            ? context.PreEntityImages["PreImage"].ToEntity<pc_Tenancy>()
                            : null;

                        var tenancy = new pc_Tenancy();

                        if (previous != null)
                        {
                            foreach (var field in previous.Attributes)
                                tenancy[field.Key] = field.Value;
                        }

                        foreach (var field in target.Attributes)
                            tenancy[field.Key] = field.Value;

                        if (tenancy.pc_StartDate.HasValue &&
                            tenancy.pc_EndDate.HasValue &&
                            tenancy.pc_EndDate.Value.Date <= tenancy.pc_StartDate.Value.Date)
                        {
                            throw new InvalidPluginExecutionException(
                                "Tenancy end date must be after start date.");
                        }
                        #region Prevent Overlapping Tenancies

                        if (tenancy.pc_Unit != null &&
                            tenancy.pc_StartDate.HasValue &&
                            tenancy.pc_EndDate.HasValue)
                        {
                            var service = localPluginContext.CurrentUserService;

                            var query = new QueryExpression(pc_Tenancy.EntityLogicalName)
                            {
                                ColumnSet = new ColumnSet(false),
                                TopCount = 1
                            };

                            // Same Unit
                            query.Criteria.AddCondition(
                                "pc_unit",
                                ConditionOperator.Equal,
                                tenancy.pc_Unit.Id);

                            // Existing Start <= New End
                            query.Criteria.AddCondition(
                                "pc_startdate",
                                ConditionOperator.LessEqual,
                                tenancy.pc_EndDate.Value.Date);

                            // Existing End >= New Start
                            query.Criteria.AddCondition(
                                "pc_enddate",
                                ConditionOperator.GreaterEqual,
                                tenancy.pc_StartDate.Value.Date);

                            // Exclude current Tenancy on Update
                            if (context.MessageName == "Update")
                            {
                                query.Criteria.AddCondition(
                                    "pc_tenancyid",
                                    ConditionOperator.NotEqual,
                                    target.Id);
                            }

                            var overlapping = service.RetrieveMultiple(query);

                            if (overlapping.Entities.Count > 0)
                            {
                                throw new InvalidPluginExecutionException(
                                    "Another tenancy already exists for this unit during the selected period.");
                            }
                        }

                        #endregion

                        break;
                    }
                #endregion

                #region Visit Validation

                case pc_Visit.EntityLogicalName:
                    {
                        var previous = context.MessageName == "Update"
                            ? context.PreEntityImages["PreImage"].ToEntity<pc_Visit>()
                            : null;

                        var visit = new pc_Visit();

                        if (previous != null)
                        {
                            foreach (var field in previous.Attributes)
                                visit[field.Key] = field.Value;
                        }

                        foreach (var field in target.Attributes)
                            visit[field.Key] = field.Value;

                        // Scheduled End must be after Scheduled Start
                        if (visit.pc_ScheduledStart.HasValue &&
                            visit.pc_ScheduledEnd.HasValue &&
                            visit.pc_ScheduledEnd.Value <= visit.pc_ScheduledStart.Value)
                        {
                            throw new InvalidPluginExecutionException(
                                "Visit end time must be after start time.");
                        }

                        // Completed Visit must have an Outcome
                        if (visit.pc_VisitStatus == pc_VisitStatus.Completed &&
                            string.IsNullOrWhiteSpace(visit.pc_Outcome))
                        {
                            throw new InvalidPluginExecutionException(
                                "Completed visit must have an outcome.");
                        }

                        break;
                    }

                #endregion

                #region Cost Approval Validation

                case pc_CostApproval.EntityLogicalName:
                    {
                        var previous = context.MessageName == "Update"
                            ? context.PreEntityImages["PreImage"]
                                .ToEntity<pc_CostApproval>()
                            : null;

                        var approval = new pc_CostApproval();

                        // Merge PreImage
                        if (previous != null)
                        {
                            foreach (var field in previous.Attributes)
                                approval[field.Key] = field.Value;
                        }

                        // Merge Target
                        foreach (var field in target.Attributes)
                            approval[field.Key] = field.Value;

                        // Approved or Rejected
                        if (approval.pc_Decision == pc_ApprovalDecision.Approved ||
                            approval.pc_Decision == pc_ApprovalDecision.Rejected)
                        {
                            if (approval.pc_DecidedBy == null)
                            {
                                throw new InvalidPluginExecutionException(
                                    "Decided By is required when a decision is made.");
                            }

                            if (!approval.pc_DecidedOn.HasValue)
                            {
                                throw new InvalidPluginExecutionException(
                                    "Decision Date is required when a decision is made.");
                            }
                        }

                        // Rejection Reason
                        if (approval.pc_Decision == pc_ApprovalDecision.Rejected &&
                            string.IsNullOrWhiteSpace(approval.pc_Reason))
                        {
                            throw new InvalidPluginExecutionException(
                                "Rejection reason is required.");
                        }

                        break;
                    }

                #endregion
            }
        }
    }
}
