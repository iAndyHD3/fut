// CiResolver.cs - Ci symbol resolver
//
// Copyright (C) 2011-2014  Piotr Fusik
//
// This file is part of CiTo, see http://cito.sourceforge.net
//
// CiTo is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// CiTo is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with CiTo.  If not, see http://www.gnu.org/licenses/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Foxoft.Ci
{

public class CiResolver : CiVisitor
{
	readonly CiProgram Program;
	CiScope CurrentScope;

	CiException StatementException(CiStatement statement, string message)
	{
		return new CiException(this.CurrentScope.Filename, statement.Line, message);
	}

	CiException StatementException(CiStatement statement, string format, params object[] args)
	{
		return StatementException(statement, string.Format(format, args));
	}

	void ResolveBase(CiClass klass)
	{
		switch (klass.VisitStatus) {
		case CiVisitStatus.NotYet:
			break;
		case CiVisitStatus.InProgress:
			throw new CiException(klass, "Circular inheritance for class {0}", klass.Name);
		case CiVisitStatus.Done:
			return;
		}
		if (klass.BaseClassName != null) {
			CiClass baseClass = Program.TryLookup(klass.BaseClassName) as CiClass;
			if (baseClass == null)
				throw new CiException(klass, "Base class {0} not found", klass.BaseClassName);
			klass.Parent = baseClass;
			klass.VisitStatus = CiVisitStatus.InProgress;
			ResolveBase(baseClass);
		}
		this.Program.Classes.Add(klass);
		klass.VisitStatus = CiVisitStatus.Done;

		foreach (CiConst konst in klass.Consts)
			klass.Add(konst);
		foreach (CiField field in klass.Fields)
			klass.Add(field);
		foreach (CiMethod method in klass.Methods)
			klass.Add(method);
	}

	static bool IsLong(CiType type)
	{
		if (type == CiSystem.LongType)
			return true;
		CiRangeType range = type as CiRangeType;
		return range != null && (range.Min < int.MinValue || range.Max > int.MaxValue);
	}

	CiExpr Coerce(CiExpr expr, CiType expected)
	{
		CiType got = expr.Type;
		if (got.Equals(expected))
			return expr;
		if (expected is CiNumericType) {
			if (expected == CiSystem.DoubleType) {
				if (got is CiNumericType)
					return expr;
			}
			else if (expected == CiSystem.FloatType) {
				if (got is CiNumericType && got != CiSystem.DoubleType)
					return expr;
			}
			else if (expected == CiSystem.LongType) {
				if (got is CiIntegerType)
					return expr;
			}
			else if (expected == CiSystem.IntType) {
				if (got is CiIntegerType && !IsLong(got))
					return expr;
			}
		}
		// TODO
		throw StatementException(expr, "Expected {0}, got {1}", expected, got);
	}

	static long SaturatedNeg(long a)
	{
		if (a == long.MinValue)
			return long.MaxValue;
		return -a;
	}

	static long SaturatedAdd(long a, long b)
	{
		long c = a + b;
		if (c >= 0) {
			if (a < 0 && b < 0)
				return long.MinValue;
		}
		else if (a > 0 && b > 0)
			return long.MaxValue;
		return c;
	}

	static long SaturatedSub(long a, long b)
	{
		if (b == long.MinValue)
			return a < 0 ? a ^ b : long.MaxValue;
		return SaturatedAdd(a, -b);
	}

	static long SaturatedMul(long a, long b)
	{
		if (a == 0 || b== 0)
			return 0;
		if (a == long.MinValue)
			return b >> 63 ^ a;
		if (b == long.MinValue)
			return a >> 63 ^ b;
		if (long.MaxValue / Math.Abs(a) < Math.Abs(b))
			return (a ^ b) >> 63 ^ long.MaxValue;
		return a * b;
	}

	static long SaturatedDiv(long a, long b)
	{
		if (a == long.MinValue && b == -1)
			return long.MaxValue;
		return a / b;
	}

	static long SaturatedShiftLeft(long a, long b)
	{
		if (a == 0 || b == 0)
			return a;
		if (b >= 63 || b < 0)
			return a >> 63 ^ long.MaxValue;
		int i = (int) b;
		long lost = long.MinValue >> (i - 1);
		if (a >= 0)
			return (a & lost) != 0 ? long.MaxValue : a << i;
		else
			return (a & lost) != lost ? long.MinValue : a << i;
	}

	static long SaturatedShiftRight(long a, long b)
	{
		return a >> (b >= 63 || b < 0 ? 63 : (int) b);
	}

	static CiRangeType UnsignedAnd(CiRangeType left, CiRangeType right)
	{
		long leftVariableBits = left.VariableBits;
		long rightVariableBits = right.VariableBits;
		// Bitwise "and" computes the digitwise minimum.
		// Calculate the lower bound with variable bits zeroed
		long min = (left.Min & ~leftVariableBits) & (right.Min & ~rightVariableBits);
		// Calculate upper bound with variable bits set
		long max = (left.Max | leftVariableBits) & (right.Max | rightVariableBits);
		// The upper bound will never exceed the input
		if (max > left.Max)
			max = left.Max;
		if (max > right.Max)
			max = right.Max;
		return new CiRangeType(min, max);
	}

	static CiRangeType UnsignedOr(CiRangeType left, CiRangeType right)
	{
		long leftVariableBits = left.VariableBits;
		long rightVariableBits = right.VariableBits;
		long min = (left.Min & ~leftVariableBits) | (right.Min & ~rightVariableBits);
		long max = left.Max | right.Max | CiRangeType.GetMask(left.Max & right.Max);
		// The lower bound will never be less than the input
		if (min < left.Min)
			min = left.Min;
		if (min < right.Min)
			min = right.Min;
		return new CiRangeType(min, max);
	}

	static CiRangeType UnsignedXor(CiRangeType left, CiRangeType right)
	{
		long variableBits = left.VariableBits | right.VariableBits;
		long min = (left.Min ^ right.Min) & ~variableBits;
		long max = (left.Max ^ right.Max) | variableBits;
		return new CiRangeType(min, max);
	}

	delegate CiRangeType UnsignedOp(CiRangeType left, CiRangeType right);

	CiRangeType BitwiseOp(CiRangeType left, CiRangeType right, UnsignedOp op)
	{
		CiRangeType leftNegative;
		CiRangeType leftPositive;
		left.SplitBySign(out leftNegative, out leftPositive);
		CiRangeType rightNegative;
		CiRangeType rightPositive;
		right.SplitBySign(out rightNegative, out rightPositive);
		CiRangeType result = null;
		if (leftNegative != null) {
			if (rightNegative != null)
				result = op(leftNegative, rightNegative);
			if (rightPositive != null)
				result = op(leftNegative, rightPositive).Union(result);
		}
		if (leftPositive != null) {
			if (rightNegative != null)
				result = op(leftPositive, rightNegative).Union(result);
			if (rightPositive != null)
				result = op(leftPositive, rightPositive).Union(result);
		}
		return result;
	}

	public override CiExpr Visit(CiCollection expr, CiPriority parent)
	{
		CiExpr[] items = expr.Items;
		if (items.Length == 0)
			throw StatementException(expr, "Cannot infer type of an empty array");
		items[0] = items[0].Accept(this, parent);
		CiType leftType = items[0].Type;
		for (int i = 1; i < items.Length; i++) {
			items[i] = items[i].Accept(this, parent);
			CiType rightType = items[i].Type;
			if (rightType == leftType)
				continue;
			CiRangeType leftRange = leftType as CiRangeType;
			CiRangeType rightRange = rightType as CiRangeType;
			if (leftRange != null && rightType != null)
				leftType = leftRange.Union(rightRange);
			else if (leftType == CiSystem.DoubleType || rightType == CiSystem.DoubleType)
				leftType = CiSystem.DoubleType;
			else if (leftType == CiSystem.FloatType || rightType == CiSystem.FloatType)
				leftType = CiSystem.FloatType;
			else
				throw StatementException(expr, "Cannot infer type of array");
		}
		expr.Type = new CiArrayStorageType { ElementType = leftType, Length = items.Length };
		return expr;
	}

	public override CiExpr Visit(CiVar expr, CiPriority parent)
	{
		expr.Type = ToType(expr.TypeExpr);
		if (expr.Value != null) {
			expr.Value = expr.Value.Accept(this, CiPriority.Statement);
			// TODO: typecheck
		}
		this.CurrentScope.Add(expr);
		return expr;
	}

	public override CiExpr Visit(CiLiteral expr, CiPriority parent)
	{
		return expr;
	}

	CiExpr Lookup(CiSymbolReference expr, CiScope scope)
	{
		if (expr.Symbol == null) {
			expr.Symbol = scope.TryLookup(expr.Name);
			if (expr.Symbol == null)
				throw StatementException(expr, "{0} not found", expr.Name);
			expr.Type = expr.Symbol.Type;
		}
		if (!(scope is CiEnum)) {
			CiConst konst = expr.Symbol as CiConst;
			if (konst != null) {
				ResolveConst(konst);
				if (konst.Value is CiLiteral)
					return konst.Value;
			}
		}
		return expr;
	}

	public override CiExpr Visit(CiSymbolReference expr, CiPriority parent)
	{
		return Lookup(expr, this.CurrentScope);
	}

	public override CiExpr Visit(CiPrefixExpr expr, CiPriority parent)
	{
		CiExpr inner;
		CiType type;
		CiRangeType range;
		switch (expr.Op) {
		case CiToken.Increment:
		case CiToken.Decrement:
			inner = expr.Inner.Accept(this, parent);
			if (!(inner.Type is CiNumericType))
				throw StatementException(expr, "Argument of ++/-- must be numeric");
			range = inner.Type as CiRangeType;
			// TODO: check lvalue
			if (range != null) {
				long delta = expr.Op == CiToken.Increment ? 1 : -1;
				type = new CiRangeType(range.Min + delta, range.Max + delta);
			}
			else
				type = inner.Type;
			expr.Inner = inner;
			expr.Type = type;
			return expr;
		case CiToken.Minus:
			inner = expr.Inner.Accept(this, parent);
			if (!(inner.Type is CiNumericType))
				throw StatementException(expr, "Argument of unary minus must be numeric");
			range = inner.Type as CiRangeType;
			if (range != null)
				type = range = new CiRangeType(SaturatedNeg(range.Max), SaturatedNeg(range.Min));
			else
				type = inner.Type;
			break;
		case CiToken.Tilde:
			inner = expr.Inner.Accept(this, parent);
			if (!(inner.Type is CiIntegerType))
				throw StatementException(expr, "Argument of bitwise complement must be integer");
			range = inner.Type as CiRangeType;
			if (range != null)
				type = range = new CiRangeType(~range.Max, ~range.Min);
			else
				type = inner.Type;
			break;
		case CiToken.ExclamationMark:
			inner = ResolveBool(expr.Inner);
			return new CiPrefixExpr { Op = CiToken.ExclamationMark, Inner = inner, Type = CiSystem.BoolType };
		case CiToken.New:
			return expr; // TODO
		case CiToken.Less:
		case CiToken.LessOrEqual:
			throw StatementException(expr, "Invalid expression");
		default:
			throw new NotImplementedException(expr.Op.ToString());
		}
		if (range != null && range.Min == range.Max)
			return new CiLiteral(range.Min);
		return new CiPrefixExpr { Op = expr.Op, Inner = inner, Type = type };
	}

	public override CiExpr Visit(CiPostfixExpr expr, CiPriority parent)
	{
		expr.Inner = expr.Inner.Accept(this, parent);
		switch (expr.Op) {
		case CiToken.Increment:
		case CiToken.Decrement:
			if (!(expr.Inner.Type is CiNumericType))
				throw StatementException(expr, "Argument of ++/-- must be numeric");
			expr.Type = expr.Inner.Type;
			// TODO: check lvalue
			return expr;
		case CiToken.ExclamationMark:
			throw StatementException(expr, "Unexpected '!'");
		default:
			throw new NotImplementedException(expr.Op.ToString());
		}
	}

	static CiIntegerType GetIntegerType(CiBinaryExpr expr)
	{
		if (IsLong(expr.Left.Type) || IsLong(expr.Right.Type))
			return CiSystem.LongType;
		return CiSystem.IntType;
	}

	static CiType GetNumericType(CiBinaryExpr expr)
	{
		CiType leftType = expr.Left.Type;
		CiType rightType = expr.Right.Type;
		if (leftType == CiSystem.DoubleType || rightType == CiSystem.DoubleType
		 || leftType == CiSystem.FloatType || rightType == CiSystem.FloatType)
			return CiSystem.DoubleType;
		return GetIntegerType(expr);
	}

	public override CiExpr Visit(CiBinaryExpr expr, CiPriority parent)
	{
		CiExpr left = expr.Left.Accept(this, parent);
		switch (expr.Op) {
		case CiToken.Dot:
			CiScope scope;
			CiSymbolReference leftSymbol = left as CiSymbolReference;
			CiSymbolReference rightSymbol = (CiSymbolReference) expr.Right;
			if (leftSymbol != null && leftSymbol.Symbol is CiScope)
				scope = (CiScope) leftSymbol.Symbol;
			else
				scope = left.Type;
			CiExpr result = Lookup(rightSymbol, scope);
			if (result != rightSymbol)
				return result;
			if (rightSymbol.Symbol == CiSystem.StringLength) {
				CiLiteral leftLiteral = left as CiLiteral;
				if (leftLiteral != null)
					return new CiLiteral((long) ((string) leftLiteral.Value).Length);
			}
			return new CiBinaryExpr { Left = left, Op = expr.Op, Right = rightSymbol, Type = result.Type };
		case CiToken.LeftParenthesis:
			// TODO: check arguments
			CiExpr[] arguments = expr.RightCollection;
			for (int i = 0; i < arguments.Length; i++)
				arguments[i] = arguments[i].Accept(this, CiPriority.Statement);
			expr.Type = left.Type;
			return expr;
		default:
			break;
		}
		CiExpr right = expr.Right.Accept(this, parent);
		CiType type;
		CiRangeType leftRange = left.Type as CiRangeType;
		CiRangeType rightRange = right.Type as CiRangeType;
	
		switch (expr.Op) {
		case CiToken.LeftBracket:
			// TODO: type check
//			if (!(right.Type is CiIntegerType))
//				throw StatementException(expr.Right, "Index is not an integer");
			if (IsLong(right.Type))
				throw StatementException(expr.Right, "Index is not 32-bit");
			CiArrayType array = left.Type as CiArrayType;
			if (array != null)
				type = array.ElementType;
			else if (left.Type is CiStringType) {
				type = CiSystem.CharType;
				CiLiteral leftLiteral = left as CiLiteral;
				CiLiteral rightLiteral = right as CiLiteral;
				if (leftLiteral != null && rightLiteral != null) {
					string s = (string) leftLiteral.Value;
					long i = (long) rightLiteral.Value;
					if (i >= 0 && i < s.Length)
						return new CiLiteral((long) s[(int) i]);
				}
			}
			else
				throw StatementException(expr.Left, "Indexed object is neither array or string");
			break;
		case CiToken.Plus:
			if (leftRange != null && rightRange != null) {
				type = new CiRangeType(
					SaturatedAdd(leftRange.Min, rightRange.Min),
					SaturatedAdd(leftRange.Max, rightRange.Max));
			}
			else if (left.Type is CiStringType || right.Type is CiStringType) {
				CiLiteral leftLiteral = left as CiLiteral;
				CiLiteral rightLiteral = right as CiLiteral;
				if (leftLiteral != null && rightLiteral != null)
					return new CiLiteral(Convert.ToString(leftLiteral.Value, CultureInfo.InvariantCulture) + Convert.ToString(rightLiteral.Value, CultureInfo.InvariantCulture));
				type = CiSystem.StringPtrType;
				// TODO: type check
			}
			else {
				type = GetNumericType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Minus:
			if (leftRange != null && rightRange != null) {
				type = new CiRangeType(
					SaturatedSub(leftRange.Min, rightRange.Max),
					SaturatedSub(leftRange.Max, rightRange.Min));
			}
			else {
				type = GetNumericType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Asterisk:
			if (leftRange != null && rightRange != null) {
				type = new CiRangeType(
					SaturatedMul(leftRange.Min, rightRange.Min),
					SaturatedMul(leftRange.Min, rightRange.Max),
					SaturatedMul(leftRange.Max, rightRange.Min),
					SaturatedMul(leftRange.Max, rightRange.Max));
			}
			else {
				type = GetNumericType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Slash:
			if (leftRange != null && rightRange != null) {
				long denMin = rightRange.Min;
				if (denMin == 0)
					denMin = 1;
				long denMax = rightRange.Max;
				if (denMax == 0)
					denMax = -1;
				type = new CiRangeType(
					SaturatedDiv(leftRange.Min, denMin),
					SaturatedDiv(leftRange.Min, denMax),
					SaturatedDiv(leftRange.Max, denMin),
					SaturatedDiv(leftRange.Max, denMax));
			}
			else {
				type = GetNumericType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Mod:
			if (leftRange != null && rightRange != null) {
				long den = ~Math.Min(rightRange.Min, -rightRange.Max); // max(abs(rightRange))-1
				if (den < 0)
					throw StatementException(expr, "Mod zero");
				type = new CiRangeType(
					leftRange.Min >= 0 ? 0 : Math.Max(leftRange.Min, -den),
					leftRange.Max < 0 ? 0 : Math.Min(leftRange.Max, den));
			}
			else {
				type = GetIntegerType(expr);
				// TODO: type check
			}
			break;
		case CiToken.And:
			if (leftRange != null && rightRange != null)
				type = BitwiseOp(leftRange, rightRange, UnsignedAnd);
			else {
				type = GetIntegerType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Or:
			if (leftRange != null && rightRange != null)
				type = BitwiseOp(leftRange, rightRange, UnsignedOr);
			else {
				type = GetIntegerType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Xor:
			if (leftRange != null && rightRange != null)
				type = BitwiseOp(leftRange, rightRange, UnsignedXor);
			else {
				type = GetIntegerType(expr);
				// TODO: type check
			}
			break;
		case CiToken.ShiftLeft:
			if (leftRange != null && rightRange != null) {
				if (rightRange.Min < 0)
					rightRange = new CiRangeType(0, 64);
				type = new CiRangeType(
					SaturatedShiftLeft(leftRange.Min, leftRange.Min < 0 ? rightRange.Max : rightRange.Min),
					SaturatedShiftLeft(leftRange.Max, leftRange.Max < 0 ? rightRange.Min : rightRange.Max));
			}
			else {
				type = GetIntegerType(expr);
				// TODO: type check
			}
			break;
		case CiToken.ShiftRight:
			if (leftRange != null && rightRange != null) {
				if (rightRange.Min < 0)
					rightRange = new CiRangeType(0, 64);
				type = new CiRangeType(
					SaturatedShiftRight(leftRange.Min, leftRange.Min < 0 ? rightRange.Min : rightRange.Max),
					SaturatedShiftRight(leftRange.Max, leftRange.Max < 0 ? rightRange.Max : rightRange.Min));
			}
			else {
				type = GetIntegerType(expr);
				// TODO: type check
			}
			break;
		case CiToken.Equal:
		case CiToken.NotEqual:
		case CiToken.Less:
		case CiToken.LessOrEqual:
		case CiToken.Greater:
		case CiToken.GreaterOrEqual:
		case CiToken.CondAnd:
		case CiToken.CondOr:
			// TODO
			type = CiSystem.BoolType;
			break;
		case CiToken.Assign:
		case CiToken.AddAssign:
		case CiToken.SubAssign:
		case CiToken.MulAssign:
		case CiToken.DivAssign:
		case CiToken.ModAssign:
		case CiToken.AndAssign:
		case CiToken.OrAssign:
		case CiToken.XorAssign:
		case CiToken.ShiftLeftAssign:
		case CiToken.ShiftRightAssign:
			// TODO
			expr.Left = left;
			expr.Right = right;
			expr.Type = left.Type;
			return expr;
		default:
			throw new NotImplementedException(expr.Op.ToString());
		}
		CiRangeType range = type as CiRangeType;
		if (range != null && range.Min == range.Max)
			return new CiLiteral(range.Min);
		return new CiBinaryExpr { Left = left, Op = expr.Op, Right = right, Type = type };
	}

	public override CiExpr Visit(CiCondExpr expr, CiPriority parent)
	{
		return expr; // TODO
	}

	public override void Visit(CiConst statement)
	{
		statement.Value = statement.Value.Accept(this, CiPriority.Statement);
		statement.Type = statement.Value.Type;
		this.CurrentScope.Add(statement);
	}

	public override void Visit(CiExpr statement)
	{
		statement.Accept(this, CiPriority.Statement);
	}

	CiExpr ResolveBool(CiExpr expr)
	{
		return Coerce(expr.Accept(this, CiPriority.Statement), CiSystem.BoolType);
	}

	void Resolve(CiStatement[] statements)
	{
		foreach (CiStatement statement in statements)
			statement.Accept(this);
	}

	void OpenScope(CiScope scope)
	{
		scope.Parent = this.CurrentScope;
		this.CurrentScope = scope;
	}

	void CloseScope()
	{
		this.CurrentScope = this.CurrentScope.Parent;
	}

	public override void Visit(CiBlock statement)
	{
		OpenScope(statement);
		Resolve(statement.Statements);
		// TODO
		CloseScope();
	}

	public override void Visit(CiBreak statement)
	{
		// TODO
	}

	public override void Visit(CiContinue statement)
	{
		// TODO
	}

	public override void Visit(CiDelete statement)
	{
		statement.Expr.Accept(this, CiPriority.Statement);
		// TODO
	}

	void ResolveLoop(CiLoop statement)
	{
		if (statement.Cond != null)
			statement.Cond = ResolveBool(statement.Cond);
		statement.Body.Accept(this);
	}

	public override void Visit(CiDoWhile statement)
	{
		ResolveLoop(statement);
	}

	public override void Visit(CiFor statement)
	{
		OpenScope(statement);
		if (statement.Init != null)
			statement.Init.Accept(this);
		if (statement.Advance != null)
			statement.Advance.Accept(this);
		ResolveLoop(statement);
		CloseScope();
	}

	public override void Visit(CiIf statement)
	{
		statement.Cond = ResolveBool(statement.Cond);
		statement.OnTrue.Accept(this);
		if (statement.OnFalse != null)
			statement.OnFalse.Accept(this);
		// TODO
	}

	public override void Visit(CiReturn statement)
	{
		if (statement.Value != null)
			statement.Value.Accept(this, CiPriority.Statement);
		// TODO
	}

	public override void Visit(CiSwitch statement)
	{
		// TODO
		statement.Value.Accept(this, CiPriority.Statement);
		foreach (CiCase kase in statement.Cases) {
			Resolve(kase.Body);
		}
		if (statement.DefaultBody != null) {
			Resolve(statement.DefaultBody);
		}
	}

	public override void Visit(CiThrow statement)
	{
		// TODO
	}

	public override void Visit(CiWhile statement)
	{
		ResolveLoop(statement);
	}

	static bool IsMutableType(ref CiExpr expr)
	{
		CiPostfixExpr postfix = expr as CiPostfixExpr;
		if (postfix == null || postfix.Op != CiToken.ExclamationMark)
			return false;
		expr = postfix.Inner;
		return true;
	}

	long FoldConstLong(CiExpr expr)
	{
		CiLiteral literal = expr.Accept(this, CiPriority.Statement) as CiLiteral;
		if (literal == null)
			throw StatementException(expr, "Expected constant value");
		if (literal.Value is long)
			return (long) literal.Value;
		throw StatementException(expr, "Expected integer");
	}

	int FoldConstUint(CiExpr expr)
	{
		long value = FoldConstLong(expr);
		if (value < 0)
			throw StatementException(expr, "Expected non-negative integer");
		if (value > int.MaxValue)
			throw StatementException(expr, "Integer too big");
		return (int) value;
	}

	CiRangeType ToRangeType(long min, CiExpr maxExpr, CiToken op)
	{
		long max = FoldConstLong(maxExpr);
		if (op == CiToken.Less)
			max--;
		if (min > max)
			throw StatementException(maxExpr, "Range min greated than max");
		return new CiRangeType(min, max);
	}

	CiType ToBaseType(CiExpr expr, bool mutable)
	{
		CiSymbolReference symbol = expr as CiSymbolReference;
		if (symbol != null) {
			// built-in, MyEnum, MyClass, MyClass!
			CiType type = this.Program.TryLookup(symbol.Name) as CiType;
			if (type == null)
				throw StatementException(expr, "Type {0} not found", symbol.Name);
			CiClass klass = type as CiClass;
			if (klass != null)
				return new CiClassPtrType { Name = klass.Name, Class = klass, Mutable = mutable };
			if (mutable)
				throw StatementException(expr, "Unexpected exclamation mark on non-reference type");
			return type;
		}

		CiBinaryExpr binary = expr as CiBinaryExpr;
		if (binary != null) {
			if (mutable)
				throw StatementException(expr, "Unexpected exclamation mark on non-reference type");
			switch (binary.Op) {
			case CiToken.LeftParenthesis:
				// string(), MyClass()
				if (binary.RightCollection.Length != 0)
					throw StatementException(binary.Right, "Expected empty parentheses on storage type");
				symbol = binary.Left as CiSymbolReference;
				if (symbol == null)
					throw StatementException(binary.Left, "Expected name of storage type");
				if (symbol.Name == "string")
					return CiSystem.StringStorageType;
				CiClass klass = this.Program.TryLookup(symbol.Name) as CiClass;
				if (klass == null)
					throw StatementException(expr, "Class {0} not found", symbol.Name);
				return klass;
			case CiToken.Less: // a < b
			case CiToken.LessOrEqual: // a <= b
				return ToRangeType(FoldConstLong(binary.Left), binary.Right, binary.Op);
			default:
				throw StatementException(expr, "Invalid type");
			}
		}

		CiPrefixExpr prefix = expr as CiPrefixExpr;
		if (prefix != null) {
			switch (prefix.Op) {
			case CiToken.Less: // <b
			case CiToken.LessOrEqual: // <=b
				return ToRangeType(0, prefix.Inner, prefix.Op);
			default:
				break;
			}
		}

		throw StatementException(expr, "Invalid type");
	}

	CiType ToType(CiExpr expr)
	{
		if (expr == null)
			return null; // void
		bool mutable = IsMutableType(ref expr);
		CiArrayType outerArray = null; // left-most in source
		CiArrayType innerArray = null; // right-most in source
		do {
			CiBinaryExpr binary = expr as CiBinaryExpr;
			if (binary == null || binary.Op != CiToken.LeftBracket)
				break;
			if (binary.Right != null) {
				if (mutable)
					throw StatementException(expr, "Unexpected exclamation mark on non-reference type");
				outerArray = new CiArrayStorageType { Length = FoldConstUint(binary.Right), ElementType = outerArray };
			}
			else
				outerArray = new CiArrayPtrType { Mutable = mutable, ElementType = outerArray };
			if (innerArray == null)
				innerArray = outerArray;
			expr = binary.Left;
			mutable = IsMutableType(ref expr);
		} while (outerArray is CiArrayPtrType);

		CiType baseType = ToBaseType(expr, mutable);
		if (outerArray == null)
			return baseType;
		innerArray.ElementType = baseType;
		return outerArray;
	}

	void ResolveConst(CiConst konst)
	{
		switch (konst.VisitStatus) {
		case CiVisitStatus.NotYet:
			break;
		case CiVisitStatus.InProgress:
			throw StatementException(konst, "Circular dependency in value of constant {0}", konst.Name);
		case CiVisitStatus.Done:
			return;
		}
		konst.Value = konst.Value.Accept(this, CiPriority.Statement);
		konst.Type = konst.Value.Type;
		konst.VisitStatus = CiVisitStatus.Done;
	}

	void ResolveConsts(CiClass klass)
	{
		foreach (CiConst konst in klass.Consts) {
			this.CurrentScope = klass;
			ResolveConst(konst);
		}
	}

	void ResolveTypes(CiClass klass)
	{
		this.CurrentScope = klass;
		foreach (CiField field in klass.Fields)
			field.Type = ToType(field.TypeExpr);
		foreach (CiMethod method in klass.Methods) {
			method.Type = ToType(method.TypeExpr);
			foreach (CiVar param in method.Parameters)
				param.Type = ToType(param.TypeExpr);
		}
	}

	void ResolveCode(CiClass klass)
	{
		foreach (CiMethod method in klass.Methods) {
			if (method.Body != null) {
				this.CurrentScope = method.Parameters;
				method.Body.Accept(this);
			}
		}
	}

	public CiResolver(CiProgram program)
	{
		this.Program = program;
		foreach (CiClass klass in program.OfType<CiClass>())
			ResolveBase(klass);
		foreach (CiClass klass in program.Classes)
			ResolveConsts(klass);
		foreach (CiClass klass in program.Classes)
			ResolveTypes(klass);
		foreach (CiClass klass in program.Classes)
			ResolveCode(klass);
	}
}

}
