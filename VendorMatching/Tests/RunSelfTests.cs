using System;
using System.Collections.Generic;
using System.IO;
using UiPath.CodedWorkflows;
using VendorMatching.Code;

namespace VendorMatching.Tests
{
    /// <summary>
    /// Runs the engine's behavioural tests as a workflow, so the suite can be executed from
    /// Studio, from a robot, or as an Orchestrator job without adding a test framework
    /// dependency to the project.
    ///
    /// To surface these in the Studio Test Explorer instead, add the UiPath.Testing.Activities
    /// dependency and mark the method with [TestCase] rather than [Workflow]; the body does not
    /// need to change, because a thrown exception is what marks a test case as failed.
    /// </summary>
    public class RunSelfTests : CodedWorkflow
    {
        [Workflow]
        public string Execute(string sampleDataFolder)
        {
            string folder = string.IsNullOrWhiteSpace(sampleDataFolder)
                ? Path.Combine(Directory.GetCurrentDirectory(), "Data")
                : sampleDataFolder.Trim();

            Log("Running vendor matching self-tests against sample data in " + folder);

            List<string> failures = SelfTests.RunAll(folder);

            if (failures.Count == 0)
            {
                const string Passed = "All vendor matching self-tests passed.";
                Log(Passed);
                return Passed;
            }

            foreach (string failure in failures)
            {
                Log("FAILED - " + failure);
            }

            throw new InvalidOperationException(
                failures.Count + " vendor matching self-test(s) failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, failures.ToArray()));
        }
    }
}
