
using System;
using CrmEarlyBound;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Plugins
{
    public sealed class CaseGuard : PluginBase
    {
        public CaseGuard() : base(typeof(CaseGuard))
        {
        }

        protected override void ExecuteCdsPlugin(
            ILocalPluginContext localPluginContext)
        {
            #region Plugin Context

            var context = localPluginContext.PluginExecutionContext;
            var service = localPluginContext.CurrentUserService;
            var trace = localPluginContext.TracingService;

            bool isCreate = context.MessageName == "Create";
            var target = (Entity)context.InputParameters["Target"];

            Incident previous = null;

            if (!isCreate)
            {
                previous = context.PreEntityImages["PreImage"]
                    .ToEntity<Incident>();
            }

            Incident current = Merge(target, previous)
                .ToEntity<Incident>();

            #endregion

            #region PropertyCare Classification

            if (!isCreate &&
                (previous.pc_IsPropertyCare == true) !=
                (current.pc_IsPropertyCare == true))
            {
                Fail("Cannot change PropertyCare classification.");
            }

            if (current.pc_IsPropertyCare != true)
                return;

            #endregion

            #region Required Fields

            if (string.IsNullOrWhiteSpace(current.Title) ||
                current.CustomerId == null ||
                current.pc_Property == null ||
                current.pc_Unit == null ||
                current.pc_Tenancy == null ||
                !current.pc_Category.HasValue)
            {
                Fail("Required PropertyCare fields are missing.");
            }

            if (current.CustomerId.LogicalName != Contact.EntityLogicalName)
                Fail("Customer must be a Contact.");

            #endregion

            #region Immutable Case Context

            if (!isCreate &&
                (!Same(previous.CustomerId, current.CustomerId) ||
                 !Same(previous.pc_Property, current.pc_Property) ||
                 !Same(previous.pc_Unit, current.pc_Unit) ||
                 !Same(previous.pc_Tenancy, current.pc_Tenancy)))
            {
                Fail("Case context cannot change after creation.");
            }

            #endregion

            #region Unit Validation

            var unit = service.Retrieve(
                pc_Unit.EntityLogicalName,
                current.pc_Unit.Id,
                new ColumnSet("pc_property"))
                .ToEntity<pc_Unit>();

            if (!Same(unit.pc_Property, current.pc_Property))
            {
                Fail("Unit does not belong to Property.");
            }

            #endregion

            #region Tenancy Validation

            var tenancy = service.Retrieve(
                pc_Tenancy.EntityLogicalName,
                current.pc_Tenancy.Id,
                new ColumnSet("pc_unit", "pc_resident"))
                .ToEntity<pc_Tenancy>();

            if (!Same(tenancy.pc_Unit, current.pc_Unit) ||
                !Same(tenancy.pc_Resident, current.CustomerId))
            {
                Fail("Tenancy does not match Unit or Customer.");
            }

            #endregion

            #region First Response Date

            if (current.pc_FirstResponseOn.HasValue)
            {
                DateTime response = current.pc_FirstResponseOn.Value;

                if (response.Kind == DateTimeKind.Unspecified)
                {
                    response = DateTime.SpecifyKind(
                        response, DateTimeKind.Utc);
                }

                if (response.ToUniversalTime() > DateTime.UtcNow)
                {
                    Fail("First response cannot be in the future.");
                }
            }

            #endregion

            #region Response Deadline

            if (target.Contains("pc_responseby"))
            {
                Fail("Response deadline cannot be edited.");
            }

            if (isCreate)
            {
                int hours;

                // Priority defaults to Normal in Dataverse.
                var priority = current.PriorityCode
                    ?? Incident_PriorityCode.Normal;

                switch (priority)
                {
                    case Incident_PriorityCode.High:
                        hours = 4;
                        break;

                    case Incident_PriorityCode.Normal:
                        hours = 24;
                        break;

                    case Incident_PriorityCode.Low:
                        hours = 72;
                        break;

                    default:
                        throw new InvalidPluginExecutionException(
                            "Invalid Case priority.");
                }

                target["pc_responseby"] =
                    DateTime.UtcNow.AddHours(hours);
            }

            #endregion

            #region Default Estimated Hours

            if (isCreate && !current.pc_EstimatedHours.HasValue)
            {
                target["pc_estimatedhours"] = 1m;
            }

            #endregion

            #region Escalation Protection

            if (isCreate &&
                (current.pc_Escalated == true ||
                 !string.IsNullOrWhiteSpace(
                     current.pc_EscalationReason)))
            {
                Fail("New Cases cannot start escalated.");
            }

            if (!isCreate &&
                (target.Contains("pc_escalated") ||
                 target.Contains("pc_escalationreason")))
            {
                bool fromEscalationApi = false;
                var parent = context.ParentContext;

                while (parent != null)
                {
                    if (parent.MessageName == "pc_EscalateCase")
                    {
                        fromEscalationApi = true;
                        break;
                    }

                    parent = parent.ParentContext;
                }

                if (!fromEscalationApi)
                {
                    Fail("Use the Escalate Case API.");
                }

                if (current.pc_Escalated != true ||
                    string.IsNullOrWhiteSpace(
                        current.pc_EscalationReason))
                {
                    Fail("Escalation requires a reason.");
                }
            }

            #endregion

            #region EGP Currency

            var currency = current.TransactionCurrencyId;

            if (currency == null && isCreate)
            {
                var query = new QueryExpression("transactioncurrency")
                {
                    ColumnSet = new ColumnSet("transactioncurrencyid"),
                    TopCount = 1
                };

                query.Criteria.AddCondition(
                    "isocurrencycode",
                    ConditionOperator.Equal,
                    "EGP");

                var currencies = service.RetrieveMultiple(query);

                if (currencies.Entities.Count == 0)
                {
                    Fail("EGP currency is not configured.");
                }

                currency = currencies.Entities[0].ToEntityReference();

                target["transactioncurrencyid"] = currency;
            }

            if (currency == null ||
                currency.LogicalName != "transactioncurrency")
            {
                Fail("Valid EGP currency is required.");
            }

            var currencyRecord = service.Retrieve(
                "transactioncurrency",
                currency.Id,
                new ColumnSet("isocurrencycode"));

            string currencyCode =
                currencyRecord.GetAttributeValue<string>(
                    "isocurrencycode");

            if (!string.Equals(
                currencyCode,
                "EGP",
                StringComparison.OrdinalIgnoreCase))
            {
                Fail("Only EGP currency is supported.");
            }

            #endregion

            trace.Trace("CaseGuard completed successfully.");
        }

        #region Helper Methods

        private static Entity Merge(Entity target, Incident previous)
        {
            var result = new Entity(Incident.EntityLogicalName);

            if (previous != null)
            {
                result.Id = previous.Id;

                foreach (var field in previous.Attributes)
                {
                    result[field.Key] = field.Value;
                }
            }

            if (target.Id != Guid.Empty)
            {
                result.Id = target.Id;
            }

            foreach (var field in target.Attributes)
            {
                result[field.Key] = field.Value;
            }

            return result;
        }

        private static bool Same(EntityReference a, EntityReference b)
        {
            if (a == null || b == null)
                return a == null && b == null;

            return a.Id == b.Id &&
                   string.Equals(
                       a.LogicalName,
                       b.LogicalName,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void Fail(string message)
        {
            throw new InvalidPluginExecutionException(message);
        }

        #endregion
    }
}
