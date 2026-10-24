namespace OpenMBEE.OpenSysML;

/// <summary>
/// How a <see cref="QueryConstraint.Comparison"/> compares a property against its values, the standard's
/// <c>PrimitiveConstraint.operator</c>. It has no unset member: a comparison always names its operator.
/// </summary>
public enum ComparisonOperator
{
    /// <summary><c>=</c>: the property equals any of the values.</summary>
    Equal = 1,

    /// <summary><c>&gt;</c>: the property, which must be ordered, is greater than the one value.</summary>
    Greater = 2,

    /// <summary><c>&lt;</c>: the property, which must be ordered, is less than the one value.</summary>
    Less = 3,
}
