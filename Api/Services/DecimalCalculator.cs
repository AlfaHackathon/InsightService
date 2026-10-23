using System.Globalization;
using System.Text.RegularExpressions;
using AlfaHackaton.Result;
using InsightService.Contracts.Calculations;
using InsightService.Models;

namespace InsightService.Services;

public static partial class DecimalCalculator
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalLiteral();
    public static Result<(Calculation Calculation, string Result)> Calculate(Calculation input)
    {
        if (string.IsNullOrWhiteSpace(input.Unit) ||
            input.Unit.Length > 100 ||
            string.IsNullOrWhiteSpace(input.Period) ||
            input.Period.Length > 1000 ||
            input.Expression is null)
        {
            return new(new ApiError(422, "invalid_calculation", "A bounded unit, period and expression are required."));
        }

        var count = 0;
        try
        {
            var expressionEvaluation = Evaluate(input.Expression, 1, ref count);
            if (expressionEvaluation.Error is { } expressionEvaluationError)
            {
                return new(expressionEvaluationError);
            }

            var (expression, value) = expressionEvaluation.Content!;
            return new((input with
            {
                Expression = expression
            }, Format(value)), null);
        }
        catch (OverflowException)
        {
            return new(new ApiError(422, "decimal_overflow", "The expression exceeds Decimal precision or range."));
        }
        catch (DivideByZeroException)
        {
            return new(new ApiError(422, "division_by_zero", "The expression divides by zero."));
        }
    }

    private static Result<(ExpressionNode, decimal)> Evaluate(ExpressionNode node, int depth, ref int count)
    {
        if (depth > 16 ||
            ++count > 255)
        {
            return new(new ApiError(422, "expression_budget", "Expressions are limited to depth 16 and 255 nodes."));
        }

        if (node.Kind == "value")
        {
            if (node.Value is null ||
                node.Value.Length > 100 ||
                node.Left is not null ||
                node.Right is not null ||
                !DecimalLiteral().IsMatch(node.Value) ||
                !decimal.TryParse(node.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                return new(new ApiError(422, "invalid_decimal", "value must be a finite invariant Decimal string with no child expressions."));
            }

            var normalizedInput = node.Value.Contains('.') ? node.Value.TrimEnd('0').TrimEnd('.') : node.Value;
            if (normalizedInput is "-0")
            {
                normalizedInput = "0";
            }

            if (Format(value) != normalizedInput)
            {
                return new(new ApiError(422, "decimal_precision", "The literal cannot be represented exactly by Decimal."));
            }

            return new((new ExpressionNode("value", Format(value)), value), null);
        }

        if (node.Kind is not ("add" or "subtract" or "multiply" or "divide" or "min" or "max") ||
            node.Value is not null ||
            node.Left is null ||
            node.Right is null)
        {
            return new(new ApiError(422, "invalid_expression", "An operator requires exactly left and right expressions."));
        }

        var leftEvaluation = Evaluate(node.Left, depth + 1, ref count);
        if (leftEvaluation.Error is { } leftEvaluationError)
        {
            return new(leftEvaluationError);
        }

        var (leftNode, left) = leftEvaluation.Content!;
        var rightEvaluation = Evaluate(node.Right, depth + 1, ref count);
        if (rightEvaluation.Error is { } rightEvaluationError)
        {
            return new(rightEvaluationError);
        }

        var (rightNode, right) = rightEvaluation.Content!;
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
        return new((new ExpressionNode(node.Kind, Left: leftNode, Right: rightNode), result), null);
    }

    private static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
