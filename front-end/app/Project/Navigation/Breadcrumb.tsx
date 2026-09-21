import React from "react";
import { buildRoute, Crumb } from "./routes";
import styles from "../Styles/Navigation.module.css";

interface IBreadcrumbProps {
  crumbs: Crumb[];
}

// Real anchors (href="#/…"), so keyboard, middle-click, copy-link and the back button all work;
// useHashRoute picks the change up from the address.
export default function Breadcrumb({ crumbs }: IBreadcrumbProps) {
  return (
    <nav aria-label="Breadcrumb" className={styles.breadcrumb}>
      <ol className={styles.breadcrumbList}>
        {crumbs.map((crumb, i) => {
          const last = i === crumbs.length - 1;
          return (
            <li key={`${i}-${crumb.label}`} className={styles.breadcrumbItem}>
              {last ? (
                <span aria-current="page" className={styles.breadcrumbCurrent}>{crumb.label}</span>
              ) : (
                <a href={buildRoute(crumb.route)} className={styles.breadcrumbLink}>{crumb.label}</a>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
