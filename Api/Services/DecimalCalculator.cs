using InsightService.Contracts.Calculations;
using InsightService.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InsightService.Services;

public static partial class DecimalCalculator
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalLiteral();

    public static (Calculation Calculation, string Result) Calculate(Calculation input)
    {
        if (string.IsNullOrWhiteSpace(input.Unit) || input.Unit.Length > 100 ||
            string.IsNullOrWhiteSpace(input.Period) || input.Period.Length > 1000 || input.Expression is null)
            throw new ApiException(422, "invalid_calculation", "A bounded unit, period and expression are required.");
        var count = 0;
        try
        {
            var (expression, value) = Evaluate(input.Expression, 1, ref count);
            return (input with { Expression = expression }, Format(value));
        }
        catch (OverflowException)
        { throw new ApiException(422, "decimal_overflow", "The expression exceeds Decimal precision or range."); }
        catch (DivideByZeroException)
        { throw new ApiException(422, "division_by_zero", "The expression divides by zero."); }
    }

    private static (ExpressionNode, decimal) Evaluate(ExpressionNode node, int depth, ref int count)
    {
        if (depth > 16 || ++count > 255)
            throw new ApiException(422, "expression_budget", "Expressions are limited to depth 16 and 255 nodes.");
        if (node.Kind == "value")
        {
            if (node.Value is null || node.Value.Length > 100 || node.Left is not null || node.Right is not null ||
                !DecimalLiteral().IsMatch(node.Value) || !decimal.TryParse(node.Value,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new ApiException(422, "invalid_decimal", "value must be a finite invariant Decimal string with no child expressions.");
            var normalizedInput = node.Value.Contains('.') ? node.Value.TrimEnd('0').TrimEnd('.') : node.Value;
            if (normalizedInput is "-0") normalizedInput = "0";
            if (Format(value) != normalizedInput)
                throw new ApiException(422, "decimal_precision", "The literal cannot be represented exactly by Decimal.");
            return (new ExpressionNode("value", Format(value)), value);
        }
        if (node.Kind is not ("add" or "subtract" or "multiply" or "divide" or "min" or "max") ||
            node.Value is not null || node.Left is null || node.Right is null)
            throw new ApiException(422, "invalid_expression", "An operator requires exactly left and right expressions.");
        var (leftNode, left) = Evaluate(node.Left, depth + 1, ref count);
        var (rightNode, right) = Evaluate(node.Right, depth + 1, ref count);
        var result = node.Kind switch
        {
            "add" => checked(left + right),
            "subtract" => checked(left - right),
            "multiply" => checked(left * right),
            "divide" => checked(left / right),
            "min" => Math.Min(left, right),
            "max" => Math.Max(left, right),
            _ => throw new InvalidOperationException()
        };
        return (new ExpressionNode(node.Kind, Left: leftNode, Right: rightNode), result);
    }

    private static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
