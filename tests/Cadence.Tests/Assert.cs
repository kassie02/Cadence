using System;

namespace Cadence.Tests
{
    public class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class Assert
    {
        public static void IsTrue(bool condition, string message = null)
        {
            if (!condition)
            {
                throw new AssertionException(message ?? "Expected condition to be true, but was false.");
            }
        }

        public static void IsFalse(bool condition, string message = null)
        {
            if (condition)
            {
                throw new AssertionException(message ?? "Expected condition to be false, but was true.");
            }
        }

        public static void AreEqual<T>(T expected, T actual, string message = null)
        {
            if (!object.Equals(expected, actual))
            {
                throw new AssertionException(message ?? $"Expected: {expected}, but actual was: {actual}.");
            }
        }

        public static void AreNotEqual<T>(T expected, T actual, string message = null)
        {
            if (object.Equals(expected, actual))
            {
                throw new AssertionException(message ?? $"Expected values not to be equal, but both were: {expected}.");
            }
        }

        public static void IsNotNull(object obj, string message = null)
        {
            if (obj == null)
            {
                throw new AssertionException(message ?? "Expected object not to be null, but was null.");
            }
        }

        public static void IsNull(object obj, string message = null)
        {
            if (obj != null)
            {
                throw new AssertionException(message ?? $"Expected object to be null, but was: {obj}.");
            }
        }

        public static void GreaterThanOrEqual(float actual, float boundary, string message = null)
        {
            if (actual < boundary)
            {
                throw new AssertionException(message ?? $"Expected {actual} >= {boundary}");
            }
        }
    }
}
