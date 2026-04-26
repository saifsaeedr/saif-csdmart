export type Datatable = {
  arrayRawData: any[];
  arraySearched: any[];
  arraySearchableColumns: string[];
  numberRowsPerPage: number;
  numberActivePage: number;
  stringSortBy: string;
  stringSortOrder: "ascending" | "descending";
};

export function functionCreateDatatable(opts: {
  parData?: any[];
  parRowsPerPage?: `${number}` | string | number;
  parSearchString?: string;
  parSortBy?: string;
  parSortOrder?: "ascending" | "descending";
  parActivePage?: number;
}): Datatable {
  const state = $state({
    arrayRawData: opts.parData ?? [],
    arraySearchableColumns: [] as string[],
    numberRowsPerPage: Number(opts.parRowsPerPage ?? 15),
    numberActivePage: opts.parActivePage ?? 1,
    stringSortBy: opts.parSortBy ?? "",
    stringSortOrder: opts.parSortOrder ?? "ascending",
  });
  return {
    get arrayRawData() { return state.arrayRawData; },
    set arrayRawData(v) { state.arrayRawData = Array.isArray(v) ? v : []; },
    get arraySearched() { return state.arrayRawData; },
    get arraySearchableColumns() { return state.arraySearchableColumns; },
    set arraySearchableColumns(v) { state.arraySearchableColumns = v; },
    get numberRowsPerPage() { return state.numberRowsPerPage; },
    set numberRowsPerPage(v) { state.numberRowsPerPage = v; },
    get numberActivePage() { return state.numberActivePage; },
    set numberActivePage(v) { state.numberActivePage = v; },
    get stringSortBy() { return state.stringSortBy; },
    set stringSortBy(v) { state.stringSortBy = v; },
    get stringSortOrder() { return state.stringSortOrder; },
    set stringSortOrder(v) { state.stringSortOrder = v; },
  };
}
