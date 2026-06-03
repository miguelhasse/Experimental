// Bring in all Demo types, then alias the two that clash with Orleans.Runtime.
global using RequestProcessor;
global using RequestContext = RequestProcessor.RequestContext;
global using RequestResult = RequestProcessor.RequestResult;
