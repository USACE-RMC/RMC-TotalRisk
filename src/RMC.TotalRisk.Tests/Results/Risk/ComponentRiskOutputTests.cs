using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="ComponentRiskOutput"/> reusable compute workspace.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class ComponentRiskOutputTests
{
    /// <summary>Verifies reset clears scalars and entries while retaining list instances.</summary>
    [TestMethod]
    public void Test_Reset_ClearsReusableState()
    {
        var output = new ComponentRiskOutput
        {
            ProbabilityOfFailure = 0.2d,
            ProbabilityOfNonFailure = 0.8d,
            MeanFailureConsequences = 10d,
            MeanExcessConsequences = 8d,
            NonFailureConsequences = 2d,
        };
        output.ResponseProbabilities.Add(0.2d);
        output.FailureConsequences.Add(10d);
        output.ExcessConsequences.Add(8d);
        var probabilities = output.ResponseProbabilities;

        output.Reset();

        Assert.AreSame(probabilities, output.ResponseProbabilities);
        Assert.AreEqual(0, output.ResponseProbabilities.Count);
        Assert.AreEqual(0, output.FailureConsequences.Count);
        Assert.AreEqual(0, output.ExcessConsequences.Count);
        Assert.AreEqual(0d, output.ProbabilityOfFailure);
        Assert.AreEqual(0d, output.ProbabilityOfNonFailure);
        Assert.AreEqual(0d, output.MeanFailureConsequences);
        Assert.AreEqual(0d, output.MeanExcessConsequences);
        Assert.AreEqual(0d, output.NonFailureConsequences);
    }
}
