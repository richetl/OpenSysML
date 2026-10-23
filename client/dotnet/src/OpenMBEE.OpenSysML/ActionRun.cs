using System;
using System.Collections.Generic;

namespace OpenMBEE.OpenSysML;

/// <summary>What one run of an action produced: what <see cref="Model.ExecuteActionAsync"/> answers.</summary>
public sealed record ActionRun
{
    /// <summary>Creates an action run, copying its collections.</summary>
    /// <param name="outputs">The action's output parameters, by name.</param>
    /// <param name="diagnostics">What the service reported about the run.</param>
    /// <param name="finalTime">The run's simulation clock when it ended, in seconds.</param>
    /// <param name="performerAttributes">The attributes the performing object holds when the run ends, keyed as <c>this.level</c>.</param>
    /// <param name="orderDependent">For each output an unresolved choice point touched, how many did.</param>
    /// <exception cref="ArgumentNullException">A collection is null.</exception>
    /// <exception cref="ArgumentException">A collection holds a null or repeats a name.</exception>
    public ActionRun(
        IEnumerable<KeyValuePair<string, Value>> outputs,
        IEnumerable<Diagnostic> diagnostics,
        double finalTime,
        IEnumerable<KeyValuePair<string, Value>> performerAttributes,
        IEnumerable<KeyValuePair<string, int>> orderDependent)
    {
        Outputs = new ValueDictionary<Value>(outputs);
        Diagnostics = new ValueList<Diagnostic>(diagnostics);
        FinalTime = finalTime;
        PerformerAttributes = new ValueDictionary<Value>(performerAttributes);
        OrderDependent = new ValueDictionary<int>(orderDependent);
    }

    /// <summary>The action's output parameters, by name; empty for an action producing none.</summary>
    public ValueDictionary<Value> Outputs { get; }

    /// <summary>What the service reported about the run: its choice points and the guards it could not evaluate.</summary>
    public ValueList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// The run's simulation clock when it ended, in seconds from the 0 it started at: the clock advances
    /// through every wait. A service without <see cref="Capabilities.FinalTime"/> reports 0 whatever the run waited on.
    /// </summary>
    public double FinalTime { get; }

    /// <summary>
    /// The attributes the object named by <see cref="RunOptions.PerformerSymbolId"/> holds when the run ends,
    /// keyed as an explored <see cref="Outcome"/>'s outputs key them (<c>this.level</c>); empty without a performer.
    /// </summary>
    public ValueDictionary<Value> PerformerAttributes { get; }

    /// <summary>
    /// For each output an unresolved choice point of the run touched, how many did, so its value stands only
    /// up to the order the run took; an output nothing order-dependent wrote is absent. A service without
    /// <see cref="Capabilities.OrderDependence"/> reports none.
    /// </summary>
    public ValueDictionary<int> OrderDependent { get; }
}
