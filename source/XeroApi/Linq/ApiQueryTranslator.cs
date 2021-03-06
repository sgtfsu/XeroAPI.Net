﻿using System;
using System.Linq;
using System.Linq.Expressions;

namespace XeroApi.Linq
{
    /// <summary>
    /// Translates a linq query into a <c ref="LinqQueryDescription" />.
    /// </summary>
    internal class ApiQueryTranslator : ExpressionVisitor
    {
        private LinqQueryDescription _query;
        private ApiQuerystringName _currentQueryStringName = ApiQuerystringName.Unknown;


        /// <summary>
        /// Translates the specified linq expression into a <c ref="LinqQueryDescription" />.
        /// </summary>
        /// <param name="expression">The linq expression.</param>
        /// <returns></returns>
        internal LinqQueryDescription Translate(Expression expression)
        {
            _query = new LinqQueryDescription();

            if (expression.Type.IsGenericType)
            {
                _query.ElementType = expression.Type.GetGenericArguments()[0];
            }
            
            Visit(expression);

            return _query;
        }
        
        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            switch (m.Method.Name)
            {
                case "Where":

                    using (new QuerystringScope(this, ApiQuerystringName.Where))
                    {
                        return base.VisitMethodCall(m);
                    }

                case "FirstOrDefault":
                case "First":
                case "SingleOrDefault":
                case "Single":

                    if (!string.IsNullOrEmpty(_query.ClientSideExpression) && !_query.ClientSideExpression.Equals(m.Method.Name))
                    {
                        throw new NotImplementedException("Only 1 aggregator expression can currently be performed.");
                    }

                    _query.ClientSideExpression = m.Method.Name;

                    using (new QuerystringScope(this, ApiQuerystringName.Where))
                    {
                        return base.VisitMethodCall(m);
                    }

                case "Count":

                    if (!string.IsNullOrEmpty(_query.ClientSideExpression) && !_query.ClientSideExpression.Equals(m.Method.Name))
                    {
                        throw new NotImplementedException("Only 1 aggregator expression can currently be performed.");
                    }

                    _query.ClientSideExpression = m.Method.Name;
                    return base.VisitMethodCall(m);
                    
                case "OrderBy":
                case "ThenBy":

                    using (new QuerystringScope(this, ApiQuerystringName.OrderBy))
                    {
                        return base.VisitMethodCall(m);
                    }
                    
                case "OrderByDescending":
                case "ThenByDescending":

                    using (new QuerystringScope(this, ApiQuerystringName.OrderBy))
                    {
                        var expression = base.VisitMethodCall(m);
                        Append(" DESC");
                        return expression;
                    }
                
                case "Skip":
                    using (new QuerystringScope(this, ApiQuerystringName.Skip))
                    {
                        return base.VisitMethodCall(m);
                    }

                default:

                    // If this is a method from a clr object, as opposed to an extension method,  the API server just might be able to support it.
                    if (m.Method.DeclaringType == typeof(string) || 
                        m.Method.DeclaringType == typeof(DateTime) || 
                        m.Method.DeclaringType == typeof(Guid))
                    {
                        return VisitObjectMethodCall(m);
                    }

                    throw new NotImplementedException(string.Format("The method '{0}' can't currently be used in a XeroApi WHERE querystring.", m.Method.Name));
            }
        }


        protected Expression VisitObjectMethodCall(MethodCallExpression m)
        {
            // The .Contains and .StartsWith methods on string objects are supported by the API server
            // e.g.
            //      c => c.Name.StartsWith("Jason")
            //      c => c.Name.Contains("ase")

            if (m.Method.IsStatic)
            {
                Append(m.Method.DeclaringType.Name);
            }

            Expression obj = Visit(m.Object);
            
            Append(".");
            Append(m.Method.Name);
            Append("(");
            var args = VisitExpressionList(m.Arguments);
            Append(")");
            return UpdateMethodCall(m, obj, m.Method, args);
        }


        protected override Expression VisitUnary(UnaryExpression u)
        {
            switch (u.NodeType)
            {
                case ExpressionType.Not:
                    Append("(");
                    Visit(u.Operand);
                    Append(" == false)");
                    break;
                default:
                    Visit(u.Operand);
                    break;
            }

            return u;
        }


        /// <summary>
        /// Determines whether the expression is to be rendered into the WHERE or ORDER clause
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns>
        /// 	<c>true</c> if [is not rendered into expression] [the specified expression]; otherwise, <c>false</c>.
        /// </returns>
        private bool IsNotRenderedIntoExpression(Expression expression)
        {
            BinaryExpression binaryExpression = expression as BinaryExpression;

            if (binaryExpression == null) 
                return false;

            MemberExpression mExp = binaryExpression.Left as MemberExpression;

            // Check if the LHS is an ItemId, ItemNumber or UpdatedDate. If so, record away from the main where clause.
            if (mExp != null && (mExp.Member.DeclaringType.Name == _query.ElementName))
            {
                if (mExp.Member.Name == _query.ElementIdProperty.SafeName()
                || mExp.Member.Name == _query.ElementNumberProperty.SafeName()
                || mExp.Member.Name == _query.ElementUpdatedDateProperty.SafeName())
                {
                    return true;
                }
            }

            return false;
        }


        protected override Expression VisitBinary(BinaryExpression b)
        {
            MemberExpression mExp = b.Left as MemberExpression;

            // Check if the LHS is an ItemId, ItemNumber or UpdatedDate. If so, record away from the main where clause.
            if (mExp != null && (mExp.Member.DeclaringType.Name == _query.ElementName))
            {
                if (mExp.Member.Name == _query.ElementIdProperty.SafeName())
                {
                    if (b.Right.Type == typeof (Guid?))
                        _query.ElementId = EvaluateExpression<Guid?>(b.Right).ToString();
                    else if (b.Right.Type == typeof (Guid))
                        _query.ElementId = EvaluateExpression<Guid>(b.Right).ToString();
                    return b;
                }
                if (mExp.Member.Name == _query.ElementNumberProperty.SafeName())
                {
                    var rightValue = EvaluateExpression<object>(b.Right);
                    
                    if (rightValue != null)
                    {
                        _query.ElementId = rightValue.ToString();
                        return b;
                    }                    
                }
                if (mExp.Member.Name == _query.ElementUpdatedDateProperty.SafeName())
                {
                    if (b.Right.Type == typeof (DateTime?))
                        _query.UpdatedSinceDate = EvaluateExpression<DateTime?>(b.Right);
                    else if (b.Right.Type == typeof (DateTime))
                        _query.UpdatedSinceDate = EvaluateExpression<DateTime>(b.Right);
                    return b;
                }
            }


            // http://answers.xero.com/developer/question/39411/
            // Check for rogue VB methods that have been slipped into the linq expression..
            MethodCallExpression leftMethod = b.Left as MethodCallExpression;

            if (leftMethod != null && leftMethod.Method.Name == "CompareString")
            {
                var memberExpression = leftMethod.Arguments[0];
                var valueExpression = leftMethod.Arguments[1];
                
                if (b.NodeType == ExpressionType.NotEqual)
                    return Visit(Expression.NotEqual(memberExpression, valueExpression));
                
                return Visit(Expression.Equal(memberExpression, valueExpression));
            }


            // Check if either the left or right hand side of the expression is a ItemId, ItemNumber or ElementUpdatedDate
            // binary expression. If so, visit the left and right hand sides separately.
            if (IsNotRenderedIntoExpression(b.Left))
            {
                VisitBinary((BinaryExpression)b.Left);
                Visit(b.Right);
                return b.Right;
            }  
            if (IsNotRenderedIntoExpression(b.Right)) 
            {
                Visit(b.Left);
                VisitBinary((BinaryExpression)b.Right);
                return b.Left;
            } 


            // Parse as a normal binary expression (operand1 operator operand2)
            Append("(");
            Visit(b.Left);
            switch (b.NodeType)
            {
                case ExpressionType.And:
                case ExpressionType.AndAlso:
                    Append(" AND ");
                    break;
                case ExpressionType.Or:
                case ExpressionType.OrElse:
                    Append(" OR ");
                    break;
                case ExpressionType.Equal:
                    Append(" == ");
                    break;
                case ExpressionType.NotEqual:
                    Append(" <> ");
                    break;
                case ExpressionType.LessThan:
                    Append(" < ");
                    break;
                case ExpressionType.LessThanOrEqual:
                    Append(" <= ");
                    break;
                case ExpressionType.GreaterThan:
                    Append(" > ");
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    Append(" >= ");
                    break;
                default:
                    throw new NotSupportedException(string.Format("The binary operator '{0}' is not supported", b.NodeType));
            }
            Visit(b.Right);
            Append(")");
            return b;
        }

         
        protected override Expression VisitConstant(ConstantExpression c)
        {
            IQueryable q = c.Value as IQueryable;
            if (q != null)
            {
                _query.ElementType = q.ElementType;
            }
            else if (c.Value == null)
            {
                Append("NULL");
            }
            else
            {
                switch (Type.GetTypeCode(c.Value.GetType()))
                {
                    case TypeCode.Boolean:
                        Append(((bool)c.Value) ? "true" : "false");
                        break;

                    case TypeCode.String:
                        Append("\"");
                        Append(c.Value.ToString());
                        Append("\"");
                        break;

                    case TypeCode.DateTime:
                        //DateTime value = (DateTime) c.Value;
                        //sb.Append(string.Format("DateTime({0},{1},{2})", value.Year, value.Month, value.Day));
                        //break;
                        return c;

                    case TypeCode.Object:
                        throw new NotSupportedException(string.Format("The constant for '{0}' is not supported", c.Value));

                    default:
                        Append(c.Value.ToString());
                        break;
                }
            }

            return c;
        }

        protected override NewExpression VisitNew(NewExpression nex)
        {
            EvaluateAndAppendSymbol(nex);
            return nex;
        }
        
        protected override Expression VisitMemberAccess(MemberExpression m)
        {
            if (m.Expression != null && m.Expression.NodeType == ExpressionType.Parameter)
            {
                Append(m.Member.Name);
                return m;
            }
            if (m.Expression != null && m.Expression.NodeType == ExpressionType.MemberAccess)
            {
                // TODO: This is just plain wrong and needs to be re-written
                Append(m.Member.DeclaringType.Name + "." + m.Member.Name);
                return m;
            }
            if (m.Expression != null && m.Expression.NodeType == ExpressionType.Constant)
            {
                EvaluateAndAppendSymbol(m);
                return m;
            }

            throw new NotSupportedException(string.Format("The member '{0}' is not supported", m.Member.Name));
        }



        protected void EvaluateAndAppendSymbol(Expression exp)
        {
            switch (exp.Type.Name)
            {
                case "DateTime":
                    DateTime date = EvaluateExpression<DateTime>(exp);

                    if (date.Date == date)
                        Append(string.Format("DateTime({0},{1},{2})", date.Year, date.Month, date.Day));
                    else
                        Append(string.Format("DateTime({0},{1},{2},{3},{4},{5})", date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second));

                    return;

                case "Guid":
                    Guid guid = EvaluateExpression<Guid>(exp);

                    if (guid == Guid.Empty)
                        Append("Guid.Empty");
                    else
                        Append(string.Format("Guid(\"{0}\")", guid));

                    return;

                case "String" :
                    string stringValue = EvaluateExpression<string>(exp);

                    if (stringValue == null)
                        Append("null");
                    else
                        Append(string.Format("\"{0}\"", stringValue));

                    return;

                case "Int32":
                    int shortValue = EvaluateExpression<int>(exp);
                    Append(string.Format("\"{0}\"", shortValue));
                    
                    return;

                case "Int64":
                    long longValue = EvaluateExpression<long>(exp);
                    Append(string.Format("\"{0}\"", longValue));

                    return;
            }
            
            throw new NotSupportedException(string.Format("The Expression return type '{0}' is not supported", exp.Type.Name));
        }

        private static T EvaluateExpression<T>(Expression expression)
        {
            Expression<Func<T>> lambda = Expression.Lambda<Func<T>>(expression);
            Func<T> func = lambda.Compile();
            return (func).Invoke();
        }


        private void Append(string term)
        {
            _query.AppendTerm(term, _currentQueryStringName);
        }

        private class QuerystringScope : IDisposable
        {
            private readonly ApiQuerystringName _originalQuerystringName;
            private readonly ApiQueryTranslator _queryTranslator;

            public QuerystringScope(ApiQueryTranslator queryTranslator, ApiQuerystringName querystringName)
            {
                _originalQuerystringName = queryTranslator._currentQueryStringName;
                _queryTranslator = queryTranslator;

                _queryTranslator._currentQueryStringName = querystringName;
            }

            public void Dispose()
            {
                _queryTranslator._currentQueryStringName = _originalQuerystringName;
            }
        }

    }
}
