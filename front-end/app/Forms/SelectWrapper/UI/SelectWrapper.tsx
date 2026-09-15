import { PureComponent, type ReactNode } from "react";
import Creatable from "react-select/creatable";
import type { GroupBase, MultiValue, SingleValue, StylesConfig } from "react-select";
import styles from "../Styles/SelectWrapper.module.css";
import { MdEdit as IconEdit } from "react-icons/md";

function getIsDark(): boolean {
  if (typeof document === "undefined") return false;
  return document.documentElement.dataset.theme === "dark";
}

function buildCustomStyles<TData>(isDark: boolean, hasPortal: boolean): StylesConfig<TData> {
  return {
    control: (base, state) => ({
      ...base,
      backgroundColor: isDark ? "#1f2937" : "#ffffff",
      borderColor: state.isFocused
        ? isDark ? "#60a5fa" : "#3b82f6"
        : isDark ? "#374151" : "#d1d5db",
      boxShadow: state.isFocused
        ? `0 0 0 1px ${isDark ? "#60a5fa" : "#3b82f6"}`
        : "none",
      "&:hover": { borderColor: isDark ? "#4b5563" : "#a1a1aa" },
    }),
    menu: (base) => ({
      ...base,
      backgroundColor: isDark ? "#111827" : "#fff",
      color: isDark ? "#f3f4f6" : "#111827",
      zIndex: 9999,
    }),
    option: (base, state) => ({
      ...base,
      backgroundColor: state.isFocused
        ? isDark ? "#374151" : "#f3f4f6"
        : "transparent",
      color: isDark ? "#f3f4f6" : "#111827",
    }),
    singleValue: (base) => ({
      ...base,
      color: isDark ? "#f3f4f6" : "#111827",
    }),
    input: (base) => ({
      ...base,
      color: isDark ? "#f3f4f6" : "#111827",
      margin: 0,
      padding: 0,
    }),
    menuPortal: (base) => ({ ...base, zIndex: hasPortal ? 10001 : 9999 }),
  };
}

export interface ISelectWrapperProps<TData> {
  data: TData[];
  selectedResolver: (candidate: TData) => boolean;
  valueResolver: (item: TData) => string;
  labelResolver: (item: TData) => string;
  onChange: (item: TData[]) => void;
  menuPortalTarget?: HTMLElement;
  isMulti?: boolean;
  placeholder?: string;
  required?: boolean;
  autoFocus?: boolean;
  /**
   * @deprecated The component will ignore this property and only effect search capabilities if a callback
   * is set for 'onSearch'. This remains here so old code still compiles but internally it does nothing.
   */
  isSearchable?: boolean;
  isClearable?: boolean;
  onCreateNew?: (value: string) => void | Promise<void>;
  disabled?: boolean;
  onSearch?: (value: string) => void | Promise<void>;
  onMenuOpen?: () => void;
  className?: string;
  minimumSearchChars?: number;
  /** When set, component shows display mode with warning text and edit CTA */
  warningText?: string;
  /**
   * Optional grouping for the dropdown menu. When provided (and at least one item
   * resolves to a label), options are rendered under sticky group headings in the
   * order groups first appear in `data`. Selection/value logic is unaffected —
   * it always operates on flat TData items.
   */
  groupResolver?: (item: TData) => string | undefined;
  /** Called after a selection is made, via setTimeout(0) to bypass
   *  react-select's internal focus restoration on searchable selects.
   *  Receives the newly selected items array. */
  onSelectAdvance?: (items: TData[]) => void;
  /**
   * Immediate (non-debounced) mirror of the input text. Fires on every
   * keystroke regardless of onSearch/minimumSearchChars. Useful for UI that
   * reacts live to what is typed (e.g. inline "create new" entries).
   */
  onInputValueChange?: (text: string) => void;
  /**
   * Custom renderer for dropdown option rows. When omitted, rows render the
   * labelResolver text as before. Created (`__isNew__`) entries always render
   * their label string.
   */
  formatOptionLabel?: (item: TData) => ReactNode;
}

export interface ISelectWrapperState {
  disabled?: boolean;
  isSearching: boolean;
  lastSearchText: string;
  isEditing: boolean;
}
export default class SelectWrapper<TData> extends PureComponent<
  ISelectWrapperProps<TData>,
  ISelectWrapperState
> {
  state = {
    isSearching: false,
    lastSearchText: "",
    isEditing: false,
  } as ISelectWrapperState;

  #searchDebounceTimer?: ReturnType<typeof setTimeout>;

  private get disabled(): boolean {
    return this.state.disabled === true || this.props.disabled === true;
  }

  private onValueChange = (
    selectedOptions: SingleValue<TData> | MultiValue<TData>,
  ) => {
    let result: TData[];

    if (Array.isArray(selectedOptions)) {
      result = selectedOptions;
      this.props.onChange(result);
    } else {
      if (!selectedOptions) {
        this.props.onChange([]);
        return;
      }
      result = [selectedOptions as TData];
      this.props.onChange(result);
    }

    if (this.props.warningText) {
      this.setState({ isEditing: false });
    }

    if (this.props.onSelectAdvance) {
      setTimeout(() => this.props.onSelectAdvance!(result), 0);
    }
  };

  get #canSearch(): boolean {
    return !!this.props.onSearch || !!this.props.onCreateNew;
  }

  #onSearchProxy(args: { searchText: string }) {
    if (
      !this.props.onSearch ||
      this.state.isSearching ||
      args.searchText === this.state.lastSearchText
    ) {
      return;
    }

    if (this.#searchDebounceTimer) {
      clearTimeout(this.#searchDebounceTimer);
    }

    this.#searchDebounceTimer = setTimeout(() => {
      this.setState({ isSearching: true, lastSearchText: args.searchText });

      Promise.resolve(this.props.onSearch!(args.searchText)).finally(() => {
        this.setState({ isSearching: false });
      });
    }, 500);
  }

  #onInputChangeHandler = (value: string, { action }: { action: string }) => {
    if (action !== "input-change") return;

    // Live input mirror — intentionally NOT debounced (see prop docs)
    this.props.onInputValueChange?.(value);

    if (!this.#canSearch) return;

    const effectiveMinimumSearchChars =
      (this.props.minimumSearchChars ?? 3) - 1;
    if (value && value.length > effectiveMinimumSearchChars) {
      this.#onSearchProxy({ searchText: value });
    }
  };

  private getDisplayLabel(): string | string[] {
    const { data, selectedResolver, labelResolver, isMulti } = this.props;
    if (!data) return isMulti ? [] : "";

    const selectedItems = data.filter(selectedResolver);
    const labels = selectedItems.map(labelResolver);

    return isMulti ? labels : (labels[0] ?? "");
  }

  private get isInDisplayMode(): boolean {
    return !!this.props.warningText && !this.state.isEditing;
  }

  private buildGroupedOptions(): Array<TData | GroupBase<TData>> {
    const { data, groupResolver } = this.props;
    if (!groupResolver || !data) return data ?? [];

    const hasGroups = data.some((item) => groupResolver(item) !== undefined);
    if (!hasGroups) return data;

    const ordered: Array<TData | GroupBase<TData>> = [];
    const byLabel = new Map<string, TData[]>();
    for (const item of data) {
      const label = groupResolver(item);
      if (label === undefined) {
        // Ungrouped items stay at the top level, above the groups
        ordered.push(item);
        continue;
      }
      if (!byLabel.has(label)) {
        byLabel.set(label, []);
      }
      byLabel.get(label)!.push(item);
    }
    for (const [label, options] of byLabel) {
      ordered.push({ label, options });
    }
    return ordered;
  }

  render() {
    const { data, isMulti, warningText } = this.props;
    const effectClassName = this.props.className
      ? this.props.className
      : styles.defaultClass;
    const isDark = getIsDark();

    if (this.isInDisplayMode) {
      const displayLabel = this.getDisplayLabel();
      const labelText = Array.isArray(displayLabel)
        ? displayLabel.join(", ")
        : displayLabel || "—";

      return (
        <div className={styles.displayModeContainer}>
          <div className={styles.displayModeIconWrapper}>
            <IconEdit className={styles.displayModeIcon} />
          </div>
          <div className={styles.displayModeContent}>
            <span className={styles.displayModeLabel}>{labelText}</span>
            <span className={styles.displayModeWarning}>{warningText}</span>
          </div>
          <button
            className={styles.displayModeCta}
            onClick={() => this.setState({ isEditing: true })}
          >
            Edit
          </button>
        </div>
      );
    }

    return (
      <Creatable
        isLoading={this.state.isSearching}
        options={this.buildGroupedOptions()}
        className={effectClassName}
        value={(data ?? []).filter(this.props.selectedResolver)}
        isMulti={isMulti}
        getOptionLabel={(data: TData) => {
          const maybe = data as {
            __isNew__?: boolean;
            label?: string;
          };
          if (maybe.__isNew__) return maybe.label ?? "";
          return this.props.labelResolver(data);
        }}
        getOptionValue={(data: TData) => {
          const maybe = data as {
            __isNew__?: boolean;
            value?: string;
          };
          if (maybe.__isNew__) return maybe.value ?? "";
          return this.props.valueResolver(data);
        }}
        classNamePrefix="react-select"
        menuPortalTarget={this.props.menuPortalTarget}
        styles={buildCustomStyles<TData>(isDark, !!this.props.menuPortalTarget)}
        onChange={this.onValueChange}
        isClearable={this.props.isClearable}
        required={this.props.required}
        autoFocus={this.props.autoFocus}
        isSearchable={this.#canSearch}
        placeholder={this.props.placeholder}
        menuShouldScrollIntoView
        isDisabled={this.disabled}
        onMenuOpen={this.props.onMenuOpen}
        onInputChange={this.#onInputChangeHandler}
        onCreateOption={
          this.props.onCreateNew
            ? async (value: string) => {
                this.setState({ disabled: true });
                try {
                  await this.props.onCreateNew!(value);
                } finally {
                  this.setState({ disabled: false });
                }
              }
            : undefined
        }
        formatCreateLabel={
          this.props.onCreateNew
            ? (value: string) => `Create '${value}'`
            : undefined
        }
        noOptionsMessage={
          this.#canSearch ? () => "No results found" : undefined
        }
        formatOptionLabel={
          this.props.formatOptionLabel
            ? (data: TData) => {
                const maybe = data as {
                  __isNew__?: boolean;
                  label?: string;
                };
                if (maybe.__isNew__) return maybe.label ?? "";
                return this.props.formatOptionLabel!(data);
              }
            : undefined
        }
      />
    );
  }
}
