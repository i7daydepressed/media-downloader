using System.Reflection;
using Castle.DynamicProxy;
using MediaDownloader.Annotations;
using MediaDownloader.Dto;

namespace MediaDownloader.Proxies;

// при генерации наследника выбираем только методы с SaveLinks и проверяем их контракт
public sealed class SaveLinksProxyGenerationHook : IProxyGenerationHook {
    public bool ShouldInterceptMethod(Type type, MethodInfo methodInfo) {
        if (!methodInfo.IsDefined(typeof(SaveLinksAttribute), true)) {
            return false;
        }
        // перехватчик ожидает проверенный MediaRequest, токен отмены и результат приема Task<bool>
        ParameterInfo[] parameters = methodInfo.GetParameters();
        if (methodInfo.ReturnType != typeof(Task<bool>) ||
            parameters.Count(parameter => parameter.ParameterType == typeof(MediaRequest)) != 1 ||
            parameters.Count(parameter => parameter.ParameterType == typeof(CancellationToken)) != 1) {
            throw new InvalidOperationException("SaveLinks требует Task<bool>, один MediaRequest и один CancellationToken.");
        }
        return true;
    }

    // пометка на непереопределяемом методе была бы нерабочей: отклоняем ее при создании прокси
    public void NonProxyableMemberNotification(Type type, MemberInfo memberInfo) {
        if (memberInfo.IsDefined(typeof(SaveLinksAttribute), true)) {
            throw new InvalidOperationException("Метод SaveLinks должен быть virtual и доступен наследнику.");
        }
    }

    public void MethodsInspected() { }
}
