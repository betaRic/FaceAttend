using System;

namespace FaceAttend.Services
{
    /// <summary>
    /// Generic operation result class for service layer operations.
    /// Consolidates the multiple result classes (DeviceRegistrationResult, DeviceValidationResult, etc.)
    /// into a single, reusable pattern.
    /// 
    /// IMPLEMENTATION NOTES:
    /// - Use OperationResult<T> when returning data
    /// - Use OperationResult (non-generic) for void operations
    /// - Always use factory methods (Ok, Fail, NotFound) for consistency
    /// - Timestamp is auto-set to UTC
    /// 
    /// EXAMPLES:
    ///   // Success with data
    ///   return OperationResult<int>.Ok(deviceId, "Device registered successfully");
    ///   
    ///   // Failure with error code
    ///   return OperationResult<int>.Fail("DEVICE_EXISTS", "Device already registered");
    ///   
    ///   // Not found pattern
    ///   return OperationResult<Employee>.NotFound("Employee");
    /// </summary>
    public class OperationResult<T>
    {
        /// <summary>
        /// Indicates if the operation succeeded
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// The result data (null if operation failed)
        /// </summary>
        public T Data { get; set; }
        
        /// <summary>
        /// Machine-readable error code (null if success)
        /// Examples: "DEVICE_EXISTS", "NOT_FOUND", "VALIDATION_ERROR"
        /// </summary>
        public string ErrorCode { get; set; }
        
        /// <summary>
        /// Human-readable message
        /// </summary>
        public string Message { get; set; }
        
        /// <summary>
        /// UTC timestamp of when the result was created
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Additional data that doesn't fit in the main Data property
        /// Useful for returning supplementary information alongside the primary result
        /// </summary>
        public object ExtraData { get; set; }

        /// <summary>
        /// Creates a successful result with data
        /// </summary>
        public static OperationResult<T> Ok(T data, string message = null)
        {
            return new OperationResult<T>
            {
                Success = true,
                Data = data,
                Message = message
            };
        }

        /// <summary>
        /// Creates a successful result with data and extra data
        /// </summary>
        public static OperationResult<T> Ok(T data, string message, object extraData)
        {
            return new OperationResult<T>
            {
                Success = true,
                Data = data,
                Message = message,
                ExtraData = extraData
            };
        }

        /// <summary>
        /// Creates a failed result with error code and message
        /// </summary>
        public static OperationResult<T> Fail(string errorCode, string message)
        {
            return new OperationResult<T>
            {
                Success = false,
                ErrorCode = errorCode,
                Message = message
            };
        }

        /// <summary>
        /// Creates a "not found" failure result
        /// </summary>
        public static OperationResult<T> NotFound(string entityName)
        {
            return new OperationResult<T>
            {
                Success = false,
                ErrorCode = "NOT_FOUND",
                Message = $"{entityName} not found"
            };
        }

        /// <summary>
        /// Creates a validation failure result
        /// </summary>
        public static OperationResult<T> ValidationError(string field, string message)
        {
            return new OperationResult<T>
            {
                Success = false,
                ErrorCode = "VALIDATION_ERROR",
                Message = $"{field}: {message}"
            };
        }

        /// <summary>
        /// Implicit conversion to bool for easy checking
        /// Example: if (result) { ... }
        /// </summary>
        public static implicit operator bool(OperationResult<T> result)
        {
            return result?.Success ?? false;
        }
    }

    /// <summary>
    /// Non-generic version for operations that don't return data
    /// </summary>
    public class OperationResult
    {
        public bool Success { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static OperationResult Ok(string message = null)
        {
            return new OperationResult
            {
                Success = true,
                Message = message
            };
        }

        public static OperationResult Fail(string errorCode, string message)
        {
            return new OperationResult
            {
                Success = false,
                ErrorCode = errorCode,
                Message = message
            };
        }

        public static OperationResult NotFound(string entityName)
        {
            return new OperationResult
            {
                Success = false,
                ErrorCode = "NOT_FOUND",
                Message = $"{entityName} not found"
            };
        }

        public static OperationResult ValidationError(string field, string message)
        {
            return new OperationResult
            {
                Success = false,
                ErrorCode = "VALIDATION_ERROR",
                Message = $"{field}: {message}"
            };
        }

        public static implicit operator bool(OperationResult result)
        {
            return result?.Success ?? false;
        }
    }
}
