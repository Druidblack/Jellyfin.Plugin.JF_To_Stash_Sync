using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JFToStashSync.Web;

/// <summary>
/// Registers an index.html transformation with the optional File Transformation plugin.
/// File Transformation runs in a separate collectible AssemblyLoadContext, so objects passed
/// through reflection must be created from the exact parameter type loaded by that plugin.
/// </summary>
public sealed class FileTransformationRegistrationService : BackgroundService
{
    private static readonly Guid TransformationId = Guid.Parse("e429721e-934f-4d4b-bdf2-1bbd247db01a");

    private readonly ILogger<FileTransformationRegistrationService> _logger;
    private bool _registered;
    private Assembly? _fileTransformationAssembly;

    public FileTransformationRegistrationService(ILogger<FileTransformationRegistrationService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JFToStashSync: starting File Transformation registration for the Jellyfin Web integrations.");

        // Plugin load order is not guaranteed. Jellyfin 12 loads plugins into separate collectible
        // AssemblyLoadContexts, so retry long enough for File Transformation to finish initialization.
        for (var attempt = 1; attempt <= 30 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (TryRegister(attempt))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }

        _logger.LogWarning(
            "JFToStashSync: Jellyfin Web transformation was NOT registered after 30 attempts. " +
            "File Transformation 3.x must be installed and enabled. Search the preceding JFToStashSync log entries for the exact registration error.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        TryUnregister();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private Assembly? FindFileTransformationAssembly()
    {
        Assembly? assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate =>
                candidate.GetName().Name?.Equals("Jellyfin.Plugin.FileTransformation", StringComparison.OrdinalIgnoreCase) == true);

        assembly ??= AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate =>
                candidate.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) == true);

        return assembly;
    }

    private void TryUnregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            Assembly? assembly = _fileTransformationAssembly ?? FindFileTransformationAssembly();
            Type? pluginInterface = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            MethodInfo? remove = pluginInterface?.GetMethod(
                "RemoveTransformation",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(Guid) },
                modifiers: null);

            if (remove is null)
            {
                return;
            }

            remove.Invoke(null, new object?[] { TransformationId });
            _registered = false;
            _logger.LogInformation(
                "JFToStashSync: removed Jellyfin Web File Transformation registration. transformationId={TransformationId}",
                TransformationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JFToStashSync: could not remove File Transformation registration during shutdown.");
        }
    }

    private bool TryRegister(int attempt)
    {
        try
        {
            Assembly? assembly = FindFileTransformationAssembly();
            if (assembly is null)
            {
                if (attempt == 1 || attempt % 5 == 0)
                {
                    _logger.LogInformation(
                        "JFToStashSync: File Transformation assembly is not loaded yet (attempt {Attempt}/30).",
                        attempt);
                }

                return false;
            }

            Type? pluginInterface = assembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterface is null)
            {
                _logger.LogWarning(
                    "JFToStashSync: found File Transformation assembly {Assembly}, but PluginInterface type was not found.",
                    assembly.FullName);
                return false;
            }

            // Do not use GetMethod(name) alone: future File Transformation versions may add overloads.
            MethodInfo? register = pluginInterface
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name.Equals("RegisterTransformation", StringComparison.Ordinal) &&
                    method.GetParameters().Length == 1);

            if (register is null)
            {
                _logger.LogWarning(
                    "JFToStashSync: File Transformation PluginInterface does not expose the expected RegisterTransformation(payload) method.");
                return false;
            }

            ParameterInfo parameter = register.GetParameters()[0];
            Type payloadType = parameter.ParameterType;

            // CRITICAL: File Transformation and this plugin live in separate AssemblyLoadContexts.
            // A Newtonsoft JObject created by this plugin may therefore not be assignable to the
            // Newtonsoft JObject expected by File Transformation, even though both have the same
            // full type name. Create the payload using the exact parameter type from FT's context.
            string payloadJson = JsonSerializer.Serialize(new
            {
                id = TransformationId.ToString(),
                fileNamePattern = "index.html",
                callbackAssembly = typeof(WebFileTransformation).Assembly.FullName,
                callbackClass = typeof(WebFileTransformation).FullName,
                callbackMethod = nameof(WebFileTransformation.TransformIndexHtml),
            });

            MethodInfo? parse = payloadType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            if (parse is null)
            {
                _logger.LogWarning(
                    "JFToStashSync: File Transformation RegisterTransformation parameter type {PayloadType} has no public Parse(string) method.",
                    payloadType.AssemblyQualifiedName);
                return false;
            }

            object? payload = parse.Invoke(null, new object?[] { payloadJson });
            if (payload is null)
            {
                _logger.LogWarning("JFToStashSync: failed to create File Transformation registration payload.");
                return false;
            }

            register.Invoke(null, new[] { payload });
            _registered = true;
            _fileTransformationAssembly = assembly;

            _logger.LogInformation(
                "JFToStashSync: registered Jellyfin Web integration injection with File Transformation. transformationId={TransformationId}, assembly={Assembly}, payloadType={PayloadType}",
                TransformationId,
                assembly.GetName().Name,
                payloadType.AssemblyQualifiedName);
            return true;
        }
        catch (TargetInvocationException ex)
        {
            Exception actual = ex.InnerException ?? ex;
            _logger.LogWarning(
                actual,
                "JFToStashSync: File Transformation registration failed on attempt {Attempt}/30: {Message}",
                attempt,
                actual.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "JFToStashSync: File Transformation registration failed on attempt {Attempt}/30: {Message}",
                attempt,
                ex.Message);
            return false;
        }
    }
}
